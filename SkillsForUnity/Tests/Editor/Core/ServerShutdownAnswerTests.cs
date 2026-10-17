using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// What a client reads when Stop() or a domain reload closes the listener. HttpListener ends an unanswered connection
    /// with whatever status its response carries, 200 with an empty body by default, so the server presets 503 +
    /// Retry-After when it accepts a request, swaps it to 504 once a write starts running, and answers what it still holds
    /// before it stops. Driven through a private loopback HttpListener and local queues, never the live server's: the bytes
    /// checked are the bytes a client reads.
    /// </summary>
    [TestFixture]
    public class ServerShutdownAnswerTests
    {
        private const string NoRoute = "/server-shutdown-tests/no-such-route";

        // ---------- a connection closed before its answer ----------

        [Test]
        public void RequestClosedBeforeItRan_ReadsAsARetryable503()
        {
            using (var loopback = Loopback.Open("GET", "/skills/schema?names=gameobject_create"))
            {
                SkillsHttpServer.PresetNotExecuted(loopback.Context.Response);
                loopback.Listener.Stop();
                var reply = loopback.ReadReply();

                Assert.That(reply.StatusLine, Does.StartWith("HTTP/1.1 503"), reply.Raw);
                Assert.That(reply.Header("Retry-After"), Is.EqualTo("5"), reply.Raw);
                Assert.That(reply.Body, Is.Empty, reply.Raw);
            }
        }

        [Test]
        public void WriteClosedAfterItStarted_ReadsAsOutcomeUnknown504()
        {
            using (var loopback = Loopback.Open("POST", NoRoute))
            {
                SkillsHttpServer.PresetNotExecuted(loopback.Context.Response);
                RunJob(NewJob(loopback.Context, "POST", NoRoute));
                loopback.Listener.Stop();
                var reply = loopback.ReadReply();

                Assert.That(reply.StatusLine, Does.StartWith("HTTP/1.1 504"), reply.Raw);
                Assert.That(reply.Header("Retry-After"), Is.Null, "An unknown outcome must not invite a resend: " + reply.Raw);
            }
        }

        [Test]
        public void ReadClosedAfterItRan_StaysARetryable503()
        {
            using (var loopback = Loopback.Open("GET", NoRoute))
            {
                SkillsHttpServer.PresetNotExecuted(loopback.Context.Response);
                RunJob(NewJob(loopback.Context, "GET", NoRoute));
                loopback.Listener.Stop();
                var reply = loopback.ReadReply();

                Assert.That(reply.StatusLine, Does.StartWith("HTTP/1.1 503"), reply.Raw);
                Assert.That(reply.Header("Retry-After"), Is.EqualTo("5"), reply.Raw);
            }
        }

        // ---------- answers written over the preset ----------

        [Test]
        public void DrainedQueuedJob_IsAnsweredNotExecutedWithJson()
        {
            using (var loopback = Loopback.Open("POST", "/skill/gameobject_create"))
            {
                SkillsHttpServer.PresetNotExecuted(loopback.Context.Response);
                var job = NewJob(loopback.Context, "POST", "/skill/gameobject_create");
                int stillQueued = FailQueued(job);
                SendResponse(job);
                var reply = loopback.ReadReply();

                Assert.That(stillQueued, Is.EqualTo(0));
                Assert.That(reply.StatusLine, Does.StartWith("HTTP/1.1 503"), reply.Raw);
                Assert.That(reply.Header("Retry-After"), Is.EqualTo("5"), reply.Raw);
                Assert.That(reply.Header("Content-Type"), Does.StartWith("application/json"), reply.Raw);
                var body = JObject.Parse(reply.Body);
                Assert.That((string)body["errorCode"], Is.EqualTo("SERVER_STOPPED"));
                Assert.That((string)body["retryStrategy"], Is.EqualTo("wait_and_retry"));
                Assert.That((bool)body["details"]["executed"], Is.False);
            }
        }

        [Test]
        public void RealAnswer_ReplacesThePreset()
        {
            const string json = "{\"status\":\"success\",\"result\":{\"name\":\"Cube\"}}";
            using (var loopback = Loopback.Open("POST", "/skill/gameobject_create"))
            {
                SkillsHttpServer.PresetNotExecuted(loopback.Context.Response);
                var job = NewJob(loopback.Context, "POST", "/skill/gameobject_create");
                SetField(job, "ResponseJson", json);
                SendResponse(job);
                var reply = loopback.ReadReply();

                Assert.That(reply.StatusLine, Does.StartWith("HTTP/1.1 200"), reply.Raw);
                Assert.That(reply.Header("Retry-After"), Is.Null, "The preset must never leak into a real answer: " + reply.Raw);
                Assert.That(reply.Header("Content-Type"), Does.StartWith("application/json"), reply.Raw);
                Assert.That(reply.Body, Is.EqualTo(json));
            }
        }

        [Test]
        public void RetryableAnswer_CarriesRetryAfterFromItsBody()
        {
            using (var loopback = Loopback.Open("POST", "/skill/gameobject_create"))
            {
                SkillsHttpServer.PresetNotExecuted(loopback.Context.Response);
                var job = NewJob(loopback.Context, "POST", "/skill/gameobject_create");
                SetField(job, "StatusCode", 429);
                SetField(job, "ResponseJson", "{\"status\":\"error\",\"errorCode\":\"RATE_LIMIT\",\"retryAfterSeconds\":1}");
                SendResponse(job);
                var reply = loopback.ReadReply();

                Assert.That(reply.StatusLine, Does.StartWith("HTTP/1.1 429"), reply.Raw);
                Assert.That(reply.Header("Retry-After"), Is.EqualTo("1"), reply.Raw);
            }
        }

        [Test]
        public void BodilessAnswer_DropsThePresetHeaders()
        {
            using (var loopback = Loopback.Open("GET", "/skills"))
            {
                SkillsHttpServer.PresetNotExecuted(loopback.Context.Response);
                var job = NewJob(loopback.Context, "GET", "/skills");
                SetField(job, "StatusCode", 304);
                SendResponse(job);
                var reply = loopback.ReadReply();

                Assert.That(reply.StatusLine, Does.StartWith("HTTP/1.1 304"), reply.Raw);
                Assert.That(reply.Header("Retry-After"), Is.Null, reply.Raw);
                Assert.That(reply.Header("Content-Type"), Is.Null, reply.Raw);
            }
        }

        // ---------- the not-executed body and the drain loop ----------

        [TestCase(true, "COMPILING")]
        [TestCase(false, "SERVER_STOPPED")]
        public void NotExecutedAnswer_SaysNothingRan(bool domainReloadPending, string errorCode)
        {
            var body = JObject.Parse(SkillsHttpServer.BuildNotExecutedResponse(domainReloadPending));

            Assert.That((string)body["errorCode"], Is.EqualTo(errorCode));
            Assert.That((string)body["retryStrategy"], Is.EqualTo("wait_and_retry"));
            Assert.That((int)body["retryAfterSeconds"], Is.EqualTo(domainReloadPending
                ? SkillsHttpServer.ReloadRetryAfterSeconds : SkillsHttpServer.NotExecutedRetryAfterSeconds));
            Assert.That((bool)body["details"]["executed"], Is.False);
            Assert.That((bool)body["details"]["domainReloadPending"], Is.EqualTo(domainReloadPending));
        }

        [Test]
        public void Drain_FailsTheQueuesOnEveryPassUntilNothingIsPending()
        {
            var pending = new Queue<int>(new[] { 2, 1, 0 });
            int passes = 0, sleeps = 0;
            long now = 0;

            bool drained = SkillsHttpServer.AwaitDrained(() => pending.Dequeue(), () => passes++, () => now,
                ms => { sleeps++; now += ms * TimeSpan.TicksPerMillisecond; }, 1000);

            Assert.That(drained, Is.True);
            Assert.That(passes, Is.EqualTo(3), "A job queued between two passes must still be failed.");
            Assert.That(sleeps, Is.EqualTo(2));
        }

        [Test]
        public void Drain_GivesUpAtItsDeadline()
        {
            long now = 0;
            int passes = 0;

            bool drained = SkillsHttpServer.AwaitDrained(() => 1, () => passes++, () => now,
                ms => now += ms * TimeSpan.TicksPerMillisecond, 100);

            Assert.That(drained, Is.False);
            Assert.That(now, Is.EqualTo(100 * TimeSpan.TicksPerMillisecond), "The wait must end at its deadline, not after it.");
            Assert.That(passes, Is.GreaterThan(1));
        }

        // ---------- server internals ----------

        private static readonly Type JobType = typeof(SkillsHttpServer).GetNestedType("RequestJob", BindingFlags.NonPublic);

        private static object NewJob(HttpListenerContext context, string httpMethod, string path)
        {
            Assert.That(JobType, Is.Not.Null, "SkillsHttpServer.RequestJob was renamed.");
            var job = Activator.CreateInstance(JobType, nonPublic: true);
            SetField(job, "Context", context);
            SetField(job, "HttpMethod", httpMethod);
            SetField(job, "Path", path);
            SetField(job, "Body", string.Empty);
            SetField(job, "RequestId", "req_test");
            SetField(job, "AgentId", "tests");
            SetField(job, "StatusCode", 200);
            return job;
        }

        private static void SetField(object job, string name, object value) => JobType.GetField(name).SetValue(job, value);

        private static void RunJob(object job) => InvokeServer("RunJob", job);

        private static void SendResponse(object job) => InvokeServer("SendResponse", job);

        /// <summary>Runs the server's FailQueuedJobs on a local queue holding just this job; returns the queue's depth counter afterwards.</summary>
        private static int FailQueued(object job)
        {
            var queueType = typeof(ConcurrentQueue<>).MakeGenericType(JobType);
            var queue = Activator.CreateInstance(queueType);
            queueType.GetMethod("Enqueue").Invoke(queue, new[] { job });
            var args = new object[] { queue, 1 };
            InvokeServer("FailQueuedJobs", args);
            return (int)args[1];
        }

        private static void InvokeServer(string name, params object[] args)
        {
            var method = typeof(SkillsHttpServer).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(method, Is.Not.Null, $"SkillsHttpServer.{name} was renamed.");
            method.Invoke(null, args);
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

        /// <summary>One request on a private loopback listener, with its context accepted and not yet answered.</summary>
        private sealed class Loopback : IDisposable
        {
            public HttpListener Listener;
            public HttpListenerContext Context;
            private TcpClient _client;

            public static Loopback Open(string httpMethod, string pathAndQuery)
            {
                int port = FreeLoopbackPort();
                var loopback = new Loopback { Listener = new HttpListener(), _client = new TcpClient() };
                try
                {
                    loopback.Listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                    loopback.Listener.Start();
                    loopback._client.Connect(IPAddress.Loopback, port);
                    var stream = loopback._client.GetStream();
                    stream.ReadTimeout = 5000;
                    // No request body: one left unread would turn the server's close into a reset.
                    var request = Encoding.ASCII.GetBytes(
                        $"{httpMethod} {pathAndQuery} HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nConnection: close\r\nContent-Length: 0\r\n\r\n");
                    stream.Write(request, 0, request.Length);

                    var pending = loopback.Listener.BeginGetContext(null, null);
                    Assert.That(pending.AsyncWaitHandle.WaitOne(5000), Is.True, "The loopback listener never produced a context.");
                    loopback.Context = loopback.Listener.EndGetContext(pending);
                    return loopback;
                }
                catch
                {
                    loopback.Dispose();
                    throw;
                }
            }

            /// <summary>Everything the server sent before it closed the connection, parsed.</summary>
            public Reply ReadReply()
            {
                var stream = _client.GetStream();
                var bytes = new List<byte>();
                var buffer = new byte[4096];
                try
                {
                    int read;
                    while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                        for (int i = 0; i < read; i++) bytes.Add(buffer[i]);
                }
                catch (System.IO.IOException) { /* a reset after the answer: what arrived is what counts */ }

                var raw = Encoding.UTF8.GetString(bytes.ToArray());
                var reply = new Reply { Raw = raw };
                int split = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                Assert.That(split, Is.GreaterThanOrEqualTo(0), "No complete HTTP header block was received: " + raw);
                var lines = raw.Substring(0, split).Split(new[] { "\r\n" }, StringSplitOptions.None);
                reply.StatusLine = lines[0];
                for (int i = 1; i < lines.Length; i++)
                {
                    int colon = lines[i].IndexOf(':');
                    if (colon > 0)
                        reply.Headers[lines[i].Substring(0, colon).Trim()] = lines[i].Substring(colon + 1).Trim();
                }
                reply.Body = raw.Substring(split + 4);
                return reply;
            }

            public void Dispose()
            {
                try { _client?.Close(); } catch { }
                try { Listener?.Close(); } catch { }
            }

            private static int FreeLoopbackPort()
            {
                var probe = new TcpListener(IPAddress.Loopback, 0);
                probe.Start();
                try { return ((IPEndPoint)probe.LocalEndpoint).Port; }
                finally { probe.Stop(); }
            }
        }
    }
}

// Producer:Betsy
