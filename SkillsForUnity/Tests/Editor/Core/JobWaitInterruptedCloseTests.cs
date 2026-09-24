using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using NUnit.Framework;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// GET /jobs/{id}?wait= holds its response open, so a domain reload can abort the responder mid-wait; whatever closes the
    /// connection then sends the status already on the response. The responder presets 503 + Retry-After before waiting, so
    /// an interrupted wait reaches curl as a retryable 503 instead of an empty 200 it would take for success. Driven through a
    /// real loopback HttpListener: the bytes checked are the bytes a client reads.
    /// </summary>
    [TestFixture]
    public class JobWaitInterruptedCloseTests
    {
        [Test]
        public void InterruptedWait_ClosesAsARetryable503()
        {
            var reply = Exchange(() => throw new InvalidOperationException("simulated abort mid-wait"));

            Assert.That(reply.StatusLine, Does.StartWith("HTTP/1.1 503"), reply.Raw);
            Assert.That(reply.Header("Retry-After"), Is.EqualTo("2"), reply.Raw);
            Assert.That(reply.Header("Content-Type"), Does.StartWith("application/json"), reply.Raw);
            Assert.That(reply.Body, Is.Empty, "Closed without an answer: " + reply.Raw);
        }

        [Test]
        public void CompletedWait_WritesTheRealAnswerWithoutThePreset()
        {
            const string json = "{\"jobId\":\"probe\",\"status\":\"completed\",\"terminal\":true,\"waitTimedOut\":false}";
            var reply = Exchange(() => (200, json));

            Assert.That(reply.StatusLine, Does.StartWith("HTTP/1.1 200"), reply.Raw);
            Assert.That(reply.Header("Retry-After"), Is.Null, "The preset must never leak into a real answer: " + reply.Raw);
            Assert.That(reply.Body, Is.EqualTo(json));
        }

        [Test]
        public void ReloadVerdict_KeepsItsOwnStatusAndBody()
        {
            const string json = "{\"status\":\"error\",\"errorCode\":\"COMPILING\"}";
            var reply = Exchange(() => (503, json));

            Assert.That(reply.StatusLine, Does.StartWith("HTTP/1.1 503"), reply.Raw);
            Assert.That(reply.Header("Retry-After"), Is.Null, reply.Raw);
            Assert.That(reply.Body, Is.EqualTo(json));
        }

        // ---------- loopback plumbing ----------

        private sealed class Reply
        {
            public string Raw;
            public string StatusLine;
            public string Body;
            public readonly Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            public string Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;
        }

        private static Reply Exchange(Func<(int StatusCode, string Json)> waitForAnswer)
        {
            int port = FreeLoopbackPort();
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            listener.Start();
            try
            {
                using (var client = new TcpClient())
                {
                    client.Connect(IPAddress.Loopback, port);
                    var stream = client.GetStream();
                    stream.ReadTimeout = 5000;
                    var request = Encoding.ASCII.GetBytes(
                        $"GET /jobs/probe?wait=90 HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nConnection: close\r\n\r\n");
                    stream.Write(request, 0, request.Length);

                    var pending = listener.BeginGetContext(null, null);
                    Assert.That(pending.AsyncWaitHandle.WaitOne(5000), Is.True, "The loopback listener never produced a context.");
                    var context = listener.EndGetContext(pending);

                    SkillsHttpServer.RespondJobWait(context, "req_test", "tests", waitForAnswer);
                    return ReadReply(stream);
                }
            }
            finally
            {
                listener.Close();
            }
        }

        private static int FreeLoopbackPort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            try { return ((IPEndPoint)probe.LocalEndpoint).Port; }
            finally { probe.Stop(); }
        }

        /// <summary>
        /// Reads the status line, headers and body (Content-Length or chunked); stops at the end of the message even if the
        /// server keeps the socket open.
        /// </summary>
        private static Reply ReadReply(NetworkStream stream)
        {
            var bytes = new List<byte>();
            var buffer = new byte[4096];
            int headerEnd = -1;
            int contentLength = -1;
            bool chunked = false;
            while (true)
            {
                if (headerEnd >= 0 && contentLength >= 0 && bytes.Count >= headerEnd + contentLength)
                    break;
                if (chunked && Encoding.ASCII.GetString(bytes.ToArray(), headerEnd, bytes.Count - headerEnd).EndsWith("0\r\n\r\n", StringComparison.Ordinal))
                    break;
                int read = stream.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                    break;
                for (int i = 0; i < read; i++) bytes.Add(buffer[i]);

                if (headerEnd < 0)
                {
                    var text = Encoding.ASCII.GetString(bytes.ToArray());
                    int split = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                    if (split >= 0)
                    {
                        headerEnd = split + 4;
                        var headerBlock = text.Substring(0, split);
                        contentLength = ParseContentLength(headerBlock);
                        chunked = contentLength < 0 && headerBlock.IndexOf("Transfer-Encoding: chunked", StringComparison.OrdinalIgnoreCase) >= 0;
                    }
                }
            }

            var raw = Encoding.UTF8.GetString(bytes.ToArray());
            var reply = new Reply { Raw = raw };
            int bodyStart = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            Assert.That(bodyStart, Is.GreaterThanOrEqualTo(0), "No complete HTTP header block was received: " + raw);

            var lines = raw.Substring(0, bodyStart).Split(new[] { "\r\n" }, StringSplitOptions.None);
            reply.StatusLine = lines[0];
            for (int i = 1; i < lines.Length; i++)
            {
                int colon = lines[i].IndexOf(':');
                if (colon > 0)
                    reply.Headers[lines[i].Substring(0, colon).Trim()] = lines[i].Substring(colon + 1).Trim();
            }
            reply.Body = raw.Substring(bodyStart + 4);
            if (string.Equals(reply.Header("Transfer-Encoding"), "chunked", StringComparison.OrdinalIgnoreCase))
                reply.Body = DecodeChunked(reply.Body);
            return reply;
        }

        private static string DecodeChunked(string body)
        {
            var decoded = new StringBuilder();
            int position = 0;
            while (position < body.Length)
            {
                int lineEnd = body.IndexOf("\r\n", position, StringComparison.Ordinal);
                if (lineEnd < 0)
                    break;
                int size = Convert.ToInt32(body.Substring(position, lineEnd - position).Split(';')[0].Trim(), 16);
                if (size == 0)
                    break;
                decoded.Append(body, lineEnd + 2, size);
                position = lineEnd + 2 + size + 2;
            }
            return decoded.ToString();
        }

        private static int ParseContentLength(string headerBlock)
        {
            foreach (var line in headerBlock.Split(new[] { "\r\n" }, StringSplitOptions.None))
            {
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(line.Substring("Content-Length:".Length).Trim(), out var length))
                    return length;
            }
            return -1;
        }
    }
}

// Producer:Betsy
