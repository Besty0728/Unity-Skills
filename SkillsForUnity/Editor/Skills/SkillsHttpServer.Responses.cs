using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace UnitySkills
{
    public static partial class SkillsHttpServer
    {
        /// <summary>
        /// Stamps the serving project's identity on every response, so a caller that bypassed the registry
        /// (bare curl, a remembered port) can notice it reached a different Editor than intended.
        /// HTTP-thread safe: reads only the /health snapshot fields. The instance id is always ASCII; the
        /// product name may not be, and header values must be, so it is percent-encoded when needed.
        /// </summary>
        private static void AddInstanceHeaders(HttpListenerResponse response)
        {
            var instanceId = _snapInstanceId;
            if (!string.IsNullOrEmpty(instanceId))
                response.Headers.Add("X-Unity-Instance", instanceId);

            var projectName = _snapProjectName;
            if (string.IsNullOrEmpty(projectName))
                return;
            bool ascii = projectName.All(c => c >= ' ' && c < (char)127);
            response.Headers.Add("X-Unity-Project", ascii ? projectName : Uri.EscapeDataString(projectName));
        }

        private const string JsonContentType = "application/json; charset=utf-8";

        /// <summary>
        /// Presets the not-executed answer's status line on a response the moment its request is accepted. HttpListener
        /// flushes whatever status a response carries when its connection is closed without an answer (a client aborting
        /// mid-upload, Stop() during a domain reload): left alone that is 200 with an empty body, which reads as success.
        /// HTTP-thread safe: no Unity API.
        /// </summary>
        internal static void PresetNotExecuted(HttpListenerResponse response)
        {
            response.StatusCode = 503;
            response.Headers.Set("Retry-After", NotExecutedRetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
            response.ContentType = JsonContentType;
        }

        /// <summary>
        /// Swaps the preset to 504 once a request that may change state starts running: a connection closed after that,
        /// before the answer is written, must read as outcome unknown, never as the not-executed 503 a client resends.
        /// </summary>
        internal static void PresetOutcomeUnknown(HttpListenerResponse response)
        {
            response.StatusCode = 504;
            response.Headers.Remove("Retry-After");
        }

        /// <summary>
        /// Replaces the preset with the real answer's status line, right before the body is written: the status,
        /// Retry-After on a 429/503 whose body carries retryAfterSeconds (removed otherwise), and the JSON content type
        /// only when there is a body.
        /// </summary>
        internal static void SetAnswerStatus(HttpListenerResponse response, int statusCode, string json)
        {
            response.StatusCode = statusCode;
            string retryAfter = statusCode == 429 || statusCode == 503 ? ReadRetryAfterSeconds(json) : null;
            if (retryAfter != null)
                response.Headers.Set("Retry-After", retryAfter);
            else
                response.Headers.Remove("Retry-After");
            response.ContentType = string.IsNullOrEmpty(json) ? null : JsonContentType;
        }

        /// <summary>retryAfterSeconds of an error body as a Retry-After value; null when the body has none.</summary>
        private static string ReadRetryAfterSeconds(string json)
        {
            if (string.IsNullOrEmpty(json) || json.IndexOf("\"retryAfterSeconds\"", StringComparison.Ordinal) < 0)
                return null;
            try
            {
                var value = JObject.Parse(json)["retryAfterSeconds"];
                return value != null && value.Type == JTokenType.Integer && value.Value<long>() > 0
                    ? value.Value<long>().ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static void SendImmediateJsonResponse(HttpListenerContext context, HttpListenerRequest request, int statusCode, object payload)
        {
            HttpListenerResponse response = null;
            try
            {
                response = context.Response;
                response.Headers.Add("Access-Control-Allow-Methods", CorsAllowMethods);
                response.Headers.Add("Access-Control-Allow-Headers", CorsAllowHeaders);
                response.Headers.Add("Access-Control-Allow-Origin", "*");
                response.Headers.Add("X-Request-Id", $"req_{Interlocked.Increment(ref _requestIdCounter):X8}");
                response.Headers.Add("X-Agent-Id", DetectAgent(request));
                AddInstanceHeaders(response);

                string responseJson = JsonConvert.SerializeObject(payload, _jsonSettings);
                byte[] buffer = Encoding.UTF8.GetBytes(responseJson);
                SetAnswerStatus(response, statusCode, responseJson);
                response.ContentLength64 = buffer.Length;
                response.OutputStream.Write(buffer, 0, buffer.Length);
            }
            catch (HttpListenerException) { /* Client disconnected */ }
            catch (System.IO.IOException) { /* Client disconnected mid-write */ }
            catch (ObjectDisposedException) { /* Response already closed */ }
            catch (Exception ex)
            {
                QueueBackgroundLog("warning", $"SendImmediateJsonResponse failed: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                try { response?.Close(); } catch { }
            }
        }

        /// <summary>
        /// Writes the body as gzip when the client supports it and a compressed body is available (only the two
        /// cacheable GET endpoints carry an ETag), else plain UTF-8. The caller must have already set the status code
        /// and content type.
        /// </summary>
        private static void WriteNegotiatedBody(HttpListenerResponse response, string json, string etag, string acceptEncoding)
        {
            byte[] gzipped = etag != null && AcceptsGzip(acceptEncoding)
                ? GetOrBuildGzip(etag, json)
                : null;

            if (gzipped != null)
            {
                response.Headers.Add("Content-Encoding", "gzip");
                response.ContentLength64 = gzipped.Length;
                response.OutputStream.Write(gzipped, 0, gzipped.Length);
                return;
            }

            byte[] buffer = Encoding.UTF8.GetBytes(json);
            response.ContentLength64 = buffer.Length;
            response.OutputStream.Write(buffer, 0, buffer.Length);
        }

        /// <summary>
        /// Returns true if the client lists gzip (or "*") in Accept-Encoding and hasn't disabled it with q=0.
        /// Deliberately kept minimal — it only gates two endpoints, and real clients (requests, curl, browsers)
        /// all just send plain "gzip, deflate".
        /// </summary>
        private static bool AcceptsGzip(string acceptEncoding)
        {
            if (string.IsNullOrEmpty(acceptEncoding))
                return false;

            foreach (var raw in acceptEncoding.Split(','))
            {
                var token = raw.Trim();
                if (token.Length == 0) continue;

                int semi = token.IndexOf(';');
                var coding = (semi >= 0 ? token.Substring(0, semi) : token).Trim();
                if (!coding.Equals("gzip", StringComparison.OrdinalIgnoreCase) && coding != "*")
                    continue;

                if (semi >= 0)
                {
                    var qPart = token.Substring(semi + 1).Trim();
                    if (qPart.StartsWith("q=", StringComparison.OrdinalIgnoreCase) &&
                        double.TryParse(qPart.Substring(2),
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out double q) && q <= 0)
                        continue; // explicitly rejected — keep scanning the next token
                }
                return true;
            }
            return false;
        }

        /// <summary>
        /// Returns the gzip body of <paramref name="json"/>, compressing and caching it on first use.
        /// Returns null (meaning "send as-is, uncompressed") when: the size is below <see cref="GzipMinBytes"/>,
        /// gzip fails to shrink the content, or on any failure — compression must never make a request fail.
        ///
        /// Pure CPU and string operations, safe on the HTTP thread. See the cache declaration for key/eviction rationale.
        /// </summary>
        private static byte[] GetOrBuildGzip(string etag, string json)
        {
            if (string.IsNullOrEmpty(etag) || string.IsNullOrEmpty(json))
                return null;

            if (_gzipCache.TryGetValue(etag, out var cached))
                return cached;

            byte[] compressed = null;
            try
            {
                byte[] raw = Encoding.UTF8.GetBytes(json);
                if (raw.Length < GzipMinBytes)
                    return null; // the overhead of one extra response header would exceed what compression saves

                using (var ms = new System.IO.MemoryStream(raw.Length / 4 + 256))
                {
                    using (var gz = new System.IO.Compression.GZipStream(
                        ms, System.IO.Compression.CompressionMode.Compress, leaveOpen: true))
                    {
                        gz.Write(raw, 0, raw.Length);
                    }
                    // Must wait for the inner using to flush the gzip trailer before reading the length.
                    if (ms.Length < raw.Length)
                        compressed = ms.ToArray();
                }
            }
            catch
            {
                return null;
            }

            if (compressed == null)
                return null;

            lock (_gzipCacheLock)
            {
                if (_gzipCache.Count >= MaxGzipCacheEntries ||
                    _gzipCacheBytes + compressed.Length > MaxGzipCacheBytes)
                {
                    _gzipCache.Clear();
                    _gzipCacheBytes = 0;
                }
                if (_gzipCache.TryAdd(etag, compressed))
                    _gzipCacheBytes += compressed.Length;
            }
            return compressed;
        }

        /// <summary>
        /// Lenient If-None-Match comparison: tolerates quoted values, the W/ weak prefix, comma lists, and '*' wildcard.
        /// </summary>
        private static bool IfNoneMatchSatisfied(string ifNoneMatch, string etag)
        {
            if (string.IsNullOrEmpty(ifNoneMatch) || string.IsNullOrEmpty(etag))
                return false;

            foreach (var raw in ifNoneMatch.Split(','))
            {
                var candidate = raw.Trim();
                if (candidate == "*") return true;
                if (candidate.StartsWith("W/", StringComparison.OrdinalIgnoreCase))
                    candidate = candidate.Substring(2);
                candidate = candidate.Trim('"');
                if (string.Equals(candidate, etag, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }
    }
}

// Producer:Betsy
