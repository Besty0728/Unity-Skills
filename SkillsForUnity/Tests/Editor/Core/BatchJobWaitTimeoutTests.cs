using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using NUnit.Framework;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// <see cref="BatchJobService.Wait"/> 的超时钳制。Wait 在 Unity 主线程上自旋 sleep，所以一个
    /// 不设上限的 timeout 会把编辑器（连同 HTTP 主线程队列）冻住调用方要求的那么久。
    ///
    /// 这里不真等 30 秒：钳制表达式是 <c>Min(MaxWaitTimeoutMs, Max(100, t))</c>，测试钉住的是
    /// 常量本身、下界 100 的可观测生效、以及中段的透传。上界与下界共用同一个表达式，所以钉住
    /// 常量 + 下界 + 透传，等于钉住整条表达式而不必付 30 秒的墙钟。
    /// </summary>
    [TestFixture]
    public class BatchJobWaitTimeoutTests
    {
        /// <summary>
        /// 造一条永不推进的 job：只写进持久层，不建运行时上下文，所以 Pump 对它无事可做，
        /// 状态一直是 running，Wait 只能等到 deadline。这是唯一能在不真跑作业的前提下
        /// 观察到 deadline 计算的办法。
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
            // 直接引用常量：编译得过就证明它还在，还是 internal，值也没变。
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

                // Max(100, 1) == 100：下界生效，所以哪怕要求 1ms 也会走完一轮 100ms 的循环。
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

                // 100 < 600 < 30000：钳制在这一段是恒等的，所以 deadline 必须真的用调用方给的值。
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
        /// 已知的同步阻塞技能必须都带 LongRunning。
        ///
        /// 原先只断言集合非空，那是烟雾断言：六个里丢掉五个它照样通过。这里逐个点名 —— 但断言的是
        /// 「已知集合 ⊆ LongRunning 集合」而不是相等，所以将来新增标注不会让这条测试变成阻碍。
        ///
        /// 每个名字都先查注册：hybridclr_* / addressables_build / yooasset_build_bundles 都来自
        /// 可选包，干净 CI 工程上根本不在注册表里，硬断言会在那里假红。
        /// </summary>
        [Test]
        public void KnownBlockingSkills_AreAllMarkedLongRunning()
        {
            // 超时钳制与 LongRunning 是同一个问题的两半：一半限制调用方能要求主线程停多久，
            // 一半告诉调用方哪些技能本来就会把主线程停住。
            var knownBlocking = new[]
            {
                "navmesh_bake",              // 全量 NavMesh 烘焙
                "hybridclr_compile_dlls",    // 热更 DLL 编译
                "hybridclr_generate_all",
                "hybridclr_generate_step",
                "addressables_build",        // Addressables 打包
                "yooasset_build_bundles",    // YooAsset 打包
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
