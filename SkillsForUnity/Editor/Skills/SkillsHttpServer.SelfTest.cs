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
        private static void RunSelfTest()
        {
            if (!_isRunning) return;
            int port = _port;
            int pjqTicks = _pjqTicksSinceStart;
            SkillsLogger.LogVerbose($"[Self-Test] Starting (ProcessJobQueue ticks={pjqTicks}, listener={_listener?.IsListening})");

            ThreadPool.QueueUserWorkItem(_ =>
            {
                // Reachability test with raw TCP and retries (completely bypasses the .NET HTTP client stack)
                var hosts = new[] { "localhost", "127.0.0.1" };
                foreach (var host in hosts)
                {
                    if (!_isRunning) return;

                    string url = $"http://{host}:{port}/health";
                    bool success = false;
                    string lastError = null;
                    var connectAddresses = GetSelfTestAddresses(host);

                    for (int attempt = 1; attempt <= 3 && !success && _isRunning; attempt++)
                    {
                        if (attempt > 1) Thread.Sleep(attempt * 1500); // Back off 3s, 4.5s

                        foreach (var address in connectAddresses)
                        {
                            if (!_isRunning)
                                return;

                            try
                            {
                                if (!TryReadSelfTestResponse(address, host, port, out string response, out string error))
                                {
                                    lastError = error;
                                    continue;
                                }

                                if (response.Contains("200") && response.Contains("\"status\""))
                                {
                                    SkillsLogger.LogVerbose($"[Self-Test] {url} -> OK");
                                    success = true;
                                    break;
                                }
                                else if (response.Length > 0)
                                {
                                    var firstLine = response.Split('\n')[0].Trim();
                                    // Before warning, retry localhost against other loopback addresses first.
                                    if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) &&
                                        firstLine.IndexOf("400", StringComparison.OrdinalIgnoreCase) >= 0)
                                    {
                                        lastError = $"{firstLine} via {address}";
                                        continue;
                                    }

                                    SkillsLogger.LogWarning($"[Self-Test] {url} -> {firstLine}");
                                    success = true;
                                    break;
                                }
                                else
                                {
                                    lastError = $"Empty response via {address}";
                                }
                            }
                            catch (Exception ex)
                            {
                                lastError = $"{ex.InnerException?.Message ?? ex.Message} via {address}";
                            }
                        }
                    }

                    if (!success)
                    {
                        SkillsLogger.LogWarning($"[Self-Test] {url} -> FAILED after 3 attempts: {lastError}");
                        SkillsLogger.LogWarning($"[Self-Test] Main thread may be busy (PJQ ticks={_pjqTicksSinceStart}). External clients can connect once editor is responsive.");
                    }
                }

            });
        }

        private static List<IPAddress> GetSelfTestAddresses(string host)
        {
            var addresses = new List<IPAddress>();

            if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    foreach (var address in Dns.GetHostAddresses(host))
                    {
                        if (IPAddress.IsLoopback(address) && !addresses.Contains(address))
                            addresses.Add(address);
                    }
                }
                catch
                {
                    // Fall back to the known loopback addresses below.
                }

                addresses.Sort((left, right) =>
                {
                    int leftRank = left.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 0 : 1;
                    int rightRank = right.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 0 : 1;
                    return leftRank.CompareTo(rightRank);
                });

                if (!addresses.Contains(IPAddress.Loopback))
                    addresses.Insert(0, IPAddress.Loopback);
                if (!addresses.Contains(IPAddress.IPv6Loopback))
                    addresses.Add(IPAddress.IPv6Loopback);

                return addresses;
            }

            if (IPAddress.TryParse(host, out var parsedAddress))
            {
                addresses.Add(parsedAddress);
                return addresses;
            }

            foreach (var address in Dns.GetHostAddresses(host))
            {
                if (!addresses.Contains(address))
                    addresses.Add(address);
            }

            return addresses;
        }

        private static bool TryReadSelfTestResponse(IPAddress address, string hostHeader, int port, out string response, out string error)
        {
            response = null;
            error = null;

            using (var tcp = new System.Net.Sockets.TcpClient(address.AddressFamily))
            {
                var ar = tcp.BeginConnect(address, port, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(3000))
                {
                    tcp.Close();
                    error = "TCP connect timed out";
                    return false;
                }

                tcp.EndConnect(ar);
                tcp.ReceiveTimeout = 5000;
                tcp.SendTimeout = 2000;

                var stream = tcp.GetStream();
                var httpReq =
                    $"GET /health HTTP/1.1\r\n" +
                    $"Host: {hostHeader}:{port}\r\n" +
                    "User-Agent: UnitySkills-SelfTest\r\n" +
                    "Accept: application/json\r\n" +
                    "Connection: close\r\n\r\n";
                var reqBytes = Encoding.ASCII.GetBytes(httpReq);
                stream.Write(reqBytes, 0, reqBytes.Length);

                var sb = new StringBuilder();
                var buf = new byte[4096];
                int read;
                while ((read = stream.Read(buf, 0, buf.Length)) > 0)
                    sb.Append(Encoding.UTF8.GetString(buf, 0, read));

                response = sb.ToString();
                return true;
            }
        }
    }
}

// Producer:Betsy
