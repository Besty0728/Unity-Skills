using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using NUnit.Framework;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// The timeout clamp on <see cref="BatchJobService.Wait"/>. Wait spin-sleeps on Unity's main thread, so an unbounded timeout would freeze the editor (and the HTTP main-thread queue)
    /// for however long the caller asked for.
    ///
    /// This doesn't actually wait 30 seconds: the clamp expression is <c>Min(MaxWaitTimeoutMs, Max(100, t))</c>, and what the tests pin down is the constant itself, the observable effect of the 100 lower
    /// bound, and pass-through in the middle range. The upper and lower bounds share the same expression, so pinning the constant + lower bound + pass-through amounts to pinning the
    /// whole expression without paying a 30-second wall clock.
    /// </summary>
    [TestFixture]
    public class BatchJobWaitTimeoutTests
    {
        /// <summary>
        /// Builds a job that never advances: it's only written to the persistence layer without building a runtime context, so Pump has nothing to do with it, its status stays
        /// running forever, and Wait can only run out the deadline. This is the only way to
        /// observe the deadline computation without actually running a job.
        /// </summary>
        private static string CreateStalledJob()
        {
            var job = new BatchJobRecord
            {
                jobId = "test_stalled_" + Guid.NewGuid().ToString("N").Substring(0, 8),
                kind = "test",
                status = "running",
                currentStage = "stalled",
                startedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                updatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                totalItems = 1,
            };
            BatchPersistence.UpsertJob(job);
            return job.jobId;
        }

        private static string CreateCompletedJob()
        {
            var job = new BatchJobRecord
            {
                jobId = "test_done_" + Guid.NewGuid().ToString("N").Substring(0, 8),
                kind = "test",
                status = "completed",
                currentStage = "completed",
                progress = 100,
                startedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                updatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                totalItems = 1,
                processedItems = 1,
            };
            BatchPersistence.UpsertJob(job);
            return job.jobId;
        }

        [Test]
        public void MaxWaitTimeoutMs_IsThirtySeconds()
        {
            // Directly reference the constant: compiling successfully proves it still exists, is still internal, and its value hasn't changed.
            Assert.That(BatchJobService.MaxWaitTimeoutMs, Is.EqualTo(30000),
                "上限改了就得同步改 batch_retry_failed 的同步路径与文档里承诺的 30s。");
        }

        [Test]
        public void Wait_OnCompletedJob_ReturnsImmediately_EvenWithHugeTimeout()
        {
            var jobId = CreateCompletedJob();
            try
            {
                var sw = Stopwatch.StartNew();
                var job = BatchJobService.Wait(jobId, int.MaxValue);
                sw.Stop();

                Assert.That(job?.status, Is.EqualTo("completed"));
                Assert.That(sw.ElapsedMilliseconds, Is.LessThan(1000),
                    $"已终态的 job 必须立刻返回，实测 {sw.ElapsedMilliseconds}ms —— 否则 job_wait 会" +
                    $"按调用方给的超时把主线程冻住。");
            }
            finally
            {
                BatchPersistence.RemoveJob(jobId);
            }
        }

        [Test]
        public void Wait_OnUnknownJob_ReturnsNullImmediately()
        {
            var sw = Stopwatch.StartNew();
            var job = BatchJobService.Wait("test_no_such_job_" + Guid.NewGuid().ToString("N"), int.MaxValue);
            sw.Stop();

            Assert.That(job, Is.Null);
            Assert.That(sw.ElapsedMilliseconds, Is.LessThan(1000),
                $"不存在的 jobId 不该让调用方等待，实测 {sw.ElapsedMilliseconds}ms。");
        }

        [Test]
        public void Wait_BelowLowerBound_IsRaisedToOneHundredMs()
        {
            var jobId = CreateStalledJob();
            try
            {
                var sw = Stopwatch.StartNew();
                BatchJobService.Wait(jobId, 1);
                sw.Stop();

                // Max(100, 1) == 100: the lower bound kicks in, so even a 1ms request still runs a full 100ms cycle.
                Assert.That(sw.ElapsedMilliseconds, Is.GreaterThanOrEqualTo(90),
                    $"下界 100ms 没有生效（实测 {sw.ElapsedMilliseconds}ms）—— 过小的超时会让 Wait" +
                    $"变成一次都不 Pump 的空转。");
                Assert.That(sw.ElapsedMilliseconds, Is.LessThan(3000),
                    $"下界不该把等待放大到秒级，实测 {sw.ElapsedMilliseconds}ms。");
            }
            finally
            {
                BatchPersistence.RemoveJob(jobId);
            }
        }

        [Test]
        public void Wait_WithinClampRange_HonoursRequestedTimeout()
        {
            var jobId = CreateStalledJob();
            try
            {
                const int requested = 600;
                var sw = Stopwatch.StartNew();
                BatchJobService.Wait(jobId, requested);
                sw.Stop();

                // 100 < 600 < 30000: the clamp is the identity in this range, so the deadline must actually use the value the caller supplied.
                Assert.That(sw.ElapsedMilliseconds, Is.GreaterThanOrEqualTo(requested - 60),
                    $"中段的超时被意外缩短了，实测 {sw.ElapsedMilliseconds}ms（要求 {requested}ms）。");
                Assert.That(sw.ElapsedMilliseconds, Is.LessThan(BatchJobService.MaxWaitTimeoutMs),
                    "无论如何都不该超过上限。");
            }
            finally
            {
                BatchPersistence.RemoveJob(jobId);
            }
        }

        /// <summary>
        /// Every known synchronously-blocking skill must be tagged LongRunning.
        ///
        /// The original version only asserted the collection was non-empty, which was a smoke assertion: it would still pass if five out of six were dropped. Here each one is named
        /// individually -- but what's asserted is "the known set is a subset of the LongRunning set," not equality, so a future new annotation won't turn this test into an obstacle.
        ///
        /// Each name is checked against the registry first: hybridclr_* / addressables_build / yooasset_build_bundles all come from optional packages, and simply aren't in the
        /// registry on a clean CI project, where a hard assertion would falsely fail.
        /// </summary>
        [Test]
        public void KnownBlockingSkills_AreAllMarkedLongRunning()
        {
            // The timeout clamp and LongRunning are two halves of the same problem: one limits how long a caller can ask the main thread to stop for, the other tells the caller which
            // skills will stop the main thread in the first place.
            var knownBlocking = new[]
            {
                "navmesh_bake",              // Full NavMesh bake
                "hybridclr_compile_dlls",    // Hot-update DLL compilation
                "hybridclr_generate_all",
                "hybridclr_generate_step",
                "addressables_build",        // Addressables build
                "yooasset_build_bundles",    // YooAsset build
            };

            var longRunning = new HashSet<string>(
                SkillRouter.GetAllSkillsSnapshotUnfiltered().Where(s => s.LongRunning).Select(s => s.Name),
                StringComparer.Ordinal);

            var registered = knownBlocking.Where(SkillRouter.HasSkill).ToArray();
            Assume.That(registered, Is.Not.Empty,
                "已知阻塞技能一个都没注册（可选包全缺），这条断言无从检验。");

            var unmarked = registered.Where(name => !longRunning.Contains(name))
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray();

            Assert.That(unmarked, Is.Empty,
                $"这些技能会同步阻塞主线程却没标 LongRunning: {string.Join(", ", unmarked)}。" +
                "agent 靠这个 flag 决定是否改走异步作业路径、以及别在看似超时时重试。");
        }
    }
}

// Producer:Betsy
