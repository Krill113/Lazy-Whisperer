using System.Collections.Generic;
using System.IO;
using LWhisper.Core.Models;
using LWhisper.SpeechEngine.Diagnostics;
using LWhisper.UI.WPF.Services;
using Xunit;

namespace LWhisper.Tests
{
    /// <summary>
    /// Определение бэкенда Whisper по нативному логу.
    ///
    /// Строки взяты дословно из живого прогона Whisper.net 1.9.0 на GTX 1660 Ti (2026-09-29).
    /// Прежняя проверка искала «vulkan» в GetRuntimeInfo(), где его нет даже при работающем GPU,
    /// ставила GpuFailed=true и навсегда переводила машину на CPU (в 5–100 раз медленнее).
    /// </summary>
    public class GpuBackendDetectorTests
    {
        private static readonly string[] VulkanGpuLog =
        {
            "whisper_init_with_params_no_state: use gpu    = 1",
            "whisper_init_with_params_no_state: gpu_device = 0",
            "ggml_vulkan: Found 1 Vulkan devices:",
            "ggml_vulkan: 0 = NVIDIA GeForce GTX 1660 Ti (NVIDIA) | uma: 0 | fp16: 1 | bf16: 0 | warp size: 32 | shared memory: 49152 | int dot: 1 | matrix cores: none",
            "whisper_init_with_params_no_state: devices    = 2",
            "whisper_init_with_params_no_state: backends   = 2",
            "whisper_model_load:      Vulkan0 total size =   573.40 MB",
        };

        // Vulkan-рантайм загружен и устройство найдено, но UseGpu=false — модель на CPU
        private static readonly string[] VulkanRuntimeUseGpuOffLog =
        {
            "whisper_init_with_params_no_state: use gpu    = 0",
            "ggml_vulkan: Found 1 Vulkan devices:",
            "ggml_vulkan: 0 = NVIDIA GeForce GTX 1660 Ti (NVIDIA) | uma: 0 | fp16: 1 | bf16: 0 | warp size: 32 | shared memory: 49152 | int dot: 1 | matrix cores: none",
            "whisper_model_load:          CPU total size =   573.40 MB",
        };

        private static readonly string[] CpuRuntimeLog =
        {
            "whisper_init_with_params_no_state: use gpu    = 1",
            "whisper_init_with_params_no_state: devices    = 1",
            "whisper_model_load:          CPU total size =   573.40 MB",
            "whisper_backend_init_gpu: no GPU found",
        };

        [Fact]
        public void ModelOnVulkan_IsGpu_WithDeviceName()
        {
            var r = GpuBackendDetector.Detect(VulkanGpuLog, "Vulkan");

            Assert.Equal(GpuUsage.Gpu, r.Usage);
            Assert.Equal("Vulkan0", r.ModelBackend);
            Assert.Equal("NVIDIA GeForce GTX 1660 Ti", r.DeviceName);
        }

        [Fact]
        public void VulkanRuntimeLoaded_ButModelOnCpu_IsCpu()
        {
            // Главная ловушка: LoadedLibrary=Vulkan ещё не значит, что модель на GPU
            var r = GpuBackendDetector.Detect(VulkanRuntimeUseGpuOffLog, "Vulkan");

            Assert.Equal(GpuUsage.Cpu, r.Usage);
            Assert.Equal("CPU", r.ModelBackend);
        }

        [Fact]
        public void CpuRuntime_IsCpu()
        {
            Assert.Equal(GpuUsage.Cpu, GpuBackendDetector.Detect(CpuRuntimeLog, "Cpu").Usage);
        }

        [Fact]
        public void CudaBackend_IsGpu_WithDeviceName()
        {
            var log = new[]
            {
                "ggml_cuda_init: found 1 CUDA devices:",
                "  Device 0: NVIDIA GeForce RTX 3060, compute capability 8.6, VMM: yes",
                "whisper_model_load:        CUDA0 total size =   573.40 MB",
            };

            var r = GpuBackendDetector.Detect(log, "Cuda");

            Assert.Equal(GpuUsage.Gpu, r.Usage);
            Assert.Equal("NVIDIA GeForce RTX 3060", r.DeviceName);
        }

        [Fact]
        public void SeveralDevices_PicksRequestedIndex()
        {
            var log = new[]
            {
                "ggml_vulkan: 0 = Intel(R) UHD Graphics 630 (Intel Corporation) | uma: 1 | fp16: 1",
                "ggml_vulkan: 1 = NVIDIA GeForce GTX 1660 Ti (NVIDIA) | uma: 0 | fp16: 1",
                "whisper_model_load:      Vulkan1 total size =   573.40 MB",
            };

            Assert.Equal("NVIDIA GeForce GTX 1660 Ti", GpuBackendDetector.Detect(log, "Vulkan", gpuDeviceIndex: 1).DeviceName);
        }

        [Fact]
        public void IntegratedGpu_IsMarked()
        {
            var log = new[]
            {
                "ggml_vulkan: 0 = Intel(R) UHD Graphics 630 (Intel Corporation) | uma: 1 | fp16: 1 | bf16: 0",
                "whisper_model_load:      Vulkan0 total size =   573.40 MB",
            };

            var r = GpuBackendDetector.Detect(log, "Vulkan");

            Assert.Equal(GpuUsage.Gpu, r.Usage);
            Assert.True(r.IsIntegrated);
            Assert.Equal("Intel(R) UHD Graphics 630", r.DeviceName);
        }

        [Fact]
        public void DiscreteGpu_IsNotIntegrated()
        {
            Assert.False(GpuBackendDetector.Detect(VulkanGpuLog, "Vulkan").IsIntegrated);
        }

        [Fact]
        public void NoModelLoadLine_CpuOnlyLibrary_IsCpu()
        {
            Assert.Equal(GpuUsage.Cpu, GpuBackendDetector.Detect(Array.Empty<string>(), "CpuNoAvx").Usage);
        }

        [Theory]
        [InlineData("Vulkan")]
        [InlineData(null)]
        public void NoModelLoadLine_GpuCapableOrUnknownLibrary_IsUnknown(string? library)
        {
            // Формат лога поменялся в новой версии whisper.cpp — провал не доказан, флаг трогать нельзя
            Assert.Equal(GpuUsage.Unknown, GpuBackendDetector.Detect(new[] { "some other line" }, library).Usage);
        }

        [Fact]
        public void OldRuntimeInfoString_IsNotEvidence()
        {
            // Та самая строка, по которой старая проверка решала «GPU нет», при работающем Vulkan
            var runtimeInfo = "WHISPER : COREML = 0 | OPENVINO = 0 | CPU : SSE3 = 1 | SSSE3 = 1 | AVX = 1 | AVX2 = 1 | F16C = 1 | FMA = 1 | BMI2 = 1 | OPENMP = 1 | REPACK = 1 | ";

            Assert.Equal(GpuUsage.Gpu, GpuBackendDetector.Detect(VulkanGpuLog.Append(runtimeInfo), "Vulkan").Usage);
        }
    }

    /// <summary>
    /// Защита от сбоев драйвера. Новый экземпляр GpuSafetyGuard на том же каталоге = следующий запуск
    /// приложения; «процесс умер в окне» = окно открыто и не закрыто (Dispose не вызван).
    /// </summary>
    public class GpuSafetyGuardTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "lwhisper-tests-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        }

        private GpuSafetyGuard NextStart() => new GpuSafetyGuard(_dir);

        /// <summary>Процесс стартовал, открыл окно и умер в нём.</summary>
        private void CrashInsideWindow()
        {
            var guard = NextStart();
            guard.EvaluateStartup();
            guard.OpenWindow(); // не закрыто — процесс «умер»
        }

        [Fact]
        public void FreshMachine_GpuOn()
        {
            var guard = NextStart();

            Assert.False(guard.EvaluateStartup());
            Assert.False(guard.IsDisabled);
        }

        [Fact]
        public void OneCrash_GpuStaysOn()
        {
            CrashInsideWindow();

            var guard = NextStart();
            Assert.False(guard.EvaluateStartup());
            Assert.Equal(1, guard.CrashCount);
            Assert.False(guard.IsDisabled);
        }

        [Fact]
        public void TwoCrashesInARow_DisableGpu_NoticeOnce()
        {
            CrashInsideWindow();
            CrashInsideWindow();

            var guard = NextStart();
            Assert.True(guard.EvaluateStartup());
            Assert.True(guard.IsDisabled);

            // Следующий запуск: уведомление не повторяется, выключение держится
            var later = NextStart();
            Assert.False(later.EvaluateStartup());
            Assert.True(later.IsDisabled);
        }

        [Fact]
        public void DeathBetweenWindows_IsNotACrash()
        {
            // Регрессия N1: снятый процесс/питание вне опасного окна не засчитывается
            var guard = NextStart();
            guard.EvaluateStartup();
            guard.OpenWindow().Dispose(); // инициализация прошла
            // ...процесс снят в диспетчере задач, окна открыто не было

            Assert.False(NextStart().EvaluateStartup());
            Assert.Equal(0, NextStart().CrashCount);
        }

        [Fact]
        public void CrashThenCleanSessionThenCrash_StillDisables()
        {
            // Чистая сессия без вычисления на GPU не доказывает работоспособность и счётчик не обнуляет
            CrashInsideWindow();
            var clean = NextStart();
            clean.EvaluateStartup();
            clean.OpenWindow().Dispose();
            CrashInsideWindow();

            var guard = NextStart();
            Assert.True(guard.EvaluateStartup());
            Assert.True(guard.IsDisabled);
        }

        [Fact]
        public void ResetCount_ClearsDisable()
        {
            CrashInsideWindow();
            CrashInsideWindow();
            var guard = NextStart();
            guard.EvaluateStartup();

            guard.ResetCount();

            Assert.False(NextStart().IsDisabled);
            Assert.Equal(0, NextStart().CrashCount);
        }

        [Fact]
        public void StaleWindowDispose_KeepsNewerMarker()
        {
            // N4: окно старого распознавателя закрылось позже, чем открылось окно новой инициализации
            var guard = NextStart();
            var oldWindow = guard.OpenWindow();
            guard.OpenWindow(); // новое окно перезаписало маркер своим токеном и «умерло»
            oldWindow.Dispose();

            Assert.False(NextStart().EvaluateStartup());
            Assert.Equal(1, NextStart().CrashCount);
        }

        [Fact]
        public void UndeletableMarker_DoesNotGrowCount()
        {
            // Маркер, который не удаётся удалить, иначе засчитывался бы сбоем на каждом старте
            CrashInsideWindow();
            var marker = Path.Combine(_dir, "gpu-risk.inprogress");
            using (new FileStream(marker, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Assert.False(NextStart().EvaluateStartup());
                Assert.False(NextStart().EvaluateStartup());
            }

            Assert.Equal(0, NextStart().CrashCount);
        }

        [Fact]
        public void UnreadableCount_IsZero()
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllText(Path.Combine(_dir, "gpu-crash-count"), "мусор");

            Assert.Equal(0, NextStart().CrashCount);
        }
    }

    public class GpuComputeTrackerTests
    {
        private sealed class FakeWindow : IDisposable
        {
            public bool Closed { get; private set; }
            public void Dispose() => Closed = true;
        }

        private readonly List<FakeWindow> _windows = new List<FakeWindow>();
        private readonly List<string> _events = new List<string>();

        private GpuComputeTracker Create() => new GpuComputeTracker(
            () =>
            {
                var w = new FakeWindow();
                _windows.Add(w);
                _events.Add("open");
                return new ActionDisposable(() => { w.Dispose(); _events.Add("close"); });
            },
            () => _events.Add("proven"));

        private sealed class ActionDisposable : IDisposable
        {
            private readonly Action _action;
            public ActionDisposable(Action action) => _action = action;
            public void Dispose() => _action();
        }

        [Fact]
        public void Success_ProvesThenClosesWindow_ThenNoMoreWindows()
        {
            var tracker = Create();

            using (var scope = tracker.Enter()!) scope.MarkSuccess();

            // Счётчик сбоев обнуляется ДО снятия маркера
            Assert.Equal(new[] { "open", "proven", "close" }, _events);
            Assert.True(tracker.IsProven);
            Assert.Null(tracker.Enter());
        }

        [Fact]
        public void Failure_ClosesWindow_WindowAvailableAgain()
        {
            // Major-1 скептика: неуспешное первое вычисление не должно тратить окно
            var tracker = Create();

            tracker.Enter()!.Dispose(); // ошибка или отмена
            Assert.False(tracker.IsProven);
            // Окно закрыто, и счётчик сбоев НЕ обнулён (onProven на неудаче убил бы защиту)
            Assert.True(_windows[0].Closed);

            var again = tracker.Enter();
            Assert.NotNull(again);
            Assert.Equal(2, _windows.Count);
            Assert.Equal(new[] { "open", "close", "open" }, _events);
        }

        [Fact]
        public void ParallelComputes_WindowClosesAfterLast()
        {
            // Major-2 скептика: соседние сегменты в нативном коде остаются под окном
            var tracker = Create();

            var first = tracker.Enter()!;
            var second = tracker.Enter()!;
            first.MarkSuccess();
            first.Dispose();

            Assert.Single(_windows);
            Assert.False(_windows[0].Closed);
            Assert.DoesNotContain("proven", _events);

            second.Dispose();

            Assert.True(_windows[0].Closed);
            Assert.Contains("proven", _events);
        }

        [Fact]
        public void DoubleDispose_CountedOnce()
        {
            var tracker = Create();
            var first = tracker.Enter()!;
            var second = tracker.Enter()!;

            first.Dispose();
            first.Dispose();

            Assert.False(_windows[0].Closed);
            second.Dispose();
            Assert.True(_windows[0].Closed);
        }
    }

    public class GpuSettingsResolverTests
    {
        [Fact]
        public void CheckboxUntouched_KeepsLiveValue_DoesNotClearGuard()
        {
            // Major-3: GPU выключен защитой (показан снятым), пользователь сохранил другую настройку —
            // решение защиты не должно превратиться в выбор пользователя
            var (useGpu, clearGuard) = GpuSettingsResolver.Resolve(liveUseGpu: true, shownChecked: false, nowChecked: false);

            Assert.True(useGpu);
            Assert.False(clearGuard);
        }

        [Fact]
        public void CheckedByUser_EnablesAndClearsGuard()
        {
            var (useGpu, clearGuard) = GpuSettingsResolver.Resolve(liveUseGpu: true, shownChecked: false, nowChecked: true);

            Assert.True(useGpu);
            Assert.True(clearGuard);
        }

        [Fact]
        public void UncheckedByUser_Disables()
        {
            var (useGpu, clearGuard) = GpuSettingsResolver.Resolve(liveUseGpu: true, shownChecked: true, nowChecked: false);

            Assert.False(useGpu);
            Assert.False(clearGuard);
        }

        [Fact]
        public void Untouched_UsesLiveValueNotSnapshot()
        {
            Assert.False(GpuSettingsResolver.Resolve(liveUseGpu: false, shownChecked: true, nowChecked: true).UseGpu);
        }
    }

    /// <summary>
    /// Решения App о видеокарте. Каждый тест падает, если соответствующее решение убрать.
    /// </summary>
    public class GpuPolicyTests
    {
        [Theory]
        [InlineData(true, false, false)]
        [InlineData(false, false, true)]
        [InlineData(true, true, true)]   // выключено защитой при включённом чекбоксе
        public void ForceCpu_UserChoiceOrGuard(bool useGpu, bool guardDisabled, bool expected)
        {
            Assert.Equal(expected, GpuPolicy.ResolveForceCpu(useGpu, guardDisabled));
        }

        [Fact]
        public void Parallelism_ClippedOnCpu_ByHintOrForceCpu()
        {
            Assert.Equal(3, GpuPolicy.EffectiveParallelism(true, 3, gpuFailedHint: false, forceCpu: false));
            Assert.Equal(2, GpuPolicy.EffectiveParallelism(true, 3, gpuFailedHint: true, forceCpu: false));
            // Первый запуск после выключения защитой: подсказка ещё «GPU», но работаем на процессоре
            Assert.Equal(2, GpuPolicy.EffectiveParallelism(true, 3, gpuFailedHint: false, forceCpu: true));
            Assert.Equal(1, GpuPolicy.EffectiveParallelism(false, 3, gpuFailedHint: false, forceCpu: false));
        }

        [Fact]
        public void AfterInit_OnlyHonestCpuResetsCount_GpuOrUnknownGetsTracker()
        {
            Assert.Equal(GpuPolicy.AfterInitAction.None, GpuPolicy.AfterInit(forceCpu: true, GpuUsage.Cpu));
            Assert.Equal(GpuPolicy.AfterInitAction.ResetCrashCount, GpuPolicy.AfterInit(forceCpu: false, GpuUsage.Cpu));
            // Успешная инициализация на GPU счётчик НЕ обнуляет — только первое вычисление
            Assert.Equal(GpuPolicy.AfterInitAction.AttachComputeTracker, GpuPolicy.AfterInit(forceCpu: false, GpuUsage.Gpu));
            Assert.Equal(GpuPolicy.AfterInitAction.AttachComputeTracker, GpuPolicy.AfterInit(forceCpu: false, GpuUsage.Unknown));
        }

        [Fact]
        public void GpuFailedHint_OnlyFromProvenGpuAttempt()
        {
            Assert.True(GpuPolicy.ShouldUpdateGpuFailedHint(gpuAttempted: true, GpuUsage.Cpu));
            Assert.True(GpuPolicy.ShouldUpdateGpuFailedHint(gpuAttempted: true, GpuUsage.Gpu));
            // Выключение пользователем или защитой не должно уезжать в перемещаемый settings.json
            Assert.False(GpuPolicy.ShouldUpdateGpuFailedHint(gpuAttempted: false, GpuUsage.Cpu));
            Assert.False(GpuPolicy.ShouldUpdateGpuFailedHint(gpuAttempted: true, GpuUsage.Unknown));
        }

        [Fact]
        public void Checkbox_ShownOffWhenGuardDisabled()
        {
            Assert.True(GpuPolicy.CheckboxShown(useGpu: true, guardDisabled: false));
            Assert.False(GpuPolicy.CheckboxShown(useGpu: true, guardDisabled: true));
            Assert.False(GpuPolicy.CheckboxShown(useGpu: false, guardDisabled: false));
        }
    }

    public class SettingsCloneTests
    {
        [Fact]
        public void Clone_KeepsFieldsTheSettingsWindowDoesNotShow()
        {
            // Окно настроек копировало поля вручную и теряло не перечисленные: GpuFailed, позицию виджета
            var source = new AppSettings
            {
                GpuFailed = true,
                UseGpu = false,
                WidgetPositionX = 1436,
                WidgetPositionY = 553.6,
            };

            var copy = SettingsManager.Clone(source);

            Assert.True(copy.GpuFailed);
            Assert.False(copy.UseGpu);
            Assert.Equal(1436, copy.WidgetPositionX);
            Assert.Equal(553.6, copy.WidgetPositionY);
        }

        [Fact]
        public void Clone_StreamingIsIndependent()
        {
            // «Отмена» в окне настроек не должна менять живые настройки
            var source = new AppSettings { Streaming = new StreamingSettings { UseBeamSearch = false } };

            var copy = SettingsManager.Clone(source);
            copy.Streaming!.UseBeamSearch = true;

            Assert.False(source.Streaming.UseBeamSearch);
        }
    }
}
