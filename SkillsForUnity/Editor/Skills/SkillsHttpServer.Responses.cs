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
                response.StatusCode = statusCode;

                string responseJson = JsonConvert.SerializeObject(payload, _jsonSettings);
                byte[] buffer = Encoding.UTF8.GetBytes(responseJson);
                response.ContentType = "application/json; charset=utf-8";
                response.ContentLength64 = buffer.Length;
                response.OutputStream.Write(buffer, 0, buffer.Length);
            }
            catch (HttpListenerException) { /* Client disconnected */ }
            catch (System.IO.IOException) { /* Client disconnected mid-write */ }
            catch (ObjectDisposedException) { /* Response already closed */ }
            catch (Exception ex)
            {
                SkillsLogger.LogWarning($"SendImmediateJsonResponse failed: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                try { response?.Close(); } catch { }
            }
        }

        /// <summary>
        /// Fast-path responder for cached GET /skills and /skills/schema. Runs on the HTTP listener thread — must
        /// never touch the Unity API or SkillsLogger (only headers, hashing, compression, socket writes). Attaches an
        /// ETag header, answers If-None-Match with an empty-body 304, and serves the cached gzip body when asked.
        /// </summary>
        private static void SendCachedGetResponse(HttpListenerContext context, HttpListenerRequest request, string json, string etag)
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
                response.Headers.Add("X-Fast-Path", "true");
                response.Headers.Add("ETag", $"\"{etag}\"");
                // The same URL now has two possible response bodies (identity / gzip); without Vary,
                // an intermediate proxy might hand the gzip body to a client that never asked for compression.
                response.Headers.Add("Vary", "Accept-Encoding");

                // 304 is decided before compression: unchanged content should cost zero bytes and zero CPU, not a wasted gzip pass.
                if (IfNoneMatchSatisfied(request.Headers["If-None-Match"], etag))
                {
                    response.StatusCode = 304; // Not Modified — must not carry a response body
                    return;
                }

                response.StatusCode = 200;
                response.ContentType = "application/json; charset=utf-8";
                WriteNegotiatedBody(response, json, etag, request.Headers["Accept-Encoding"]);
            }
            catch (HttpListenerException) { /* Client disconnected */ }
            catch (System.IO.IOException) { /* Client disconnected mid-write */ }
            catch (ObjectDisposedException) { /* Response already closed */ }
            catch { /* Never let fast-path errors kill the listener loop */ }
            finally
            {
                try { response?.Close(); } catch { }
            }
        }

        /// <summary>
        /// Writes the body as gzip when the client supports it and a compressed body is available, else plain UTF-8.
        /// Shared by the HTTP-thread fast path and main-thread slow path, so content negotiation is identical between
        /// them. The caller must have already set the status code and content type.
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
