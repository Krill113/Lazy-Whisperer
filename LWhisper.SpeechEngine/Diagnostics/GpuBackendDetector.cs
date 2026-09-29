using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace LWhisper.SpeechEngine.Diagnostics
{
    /// <summary>
    /// Где фактически работает модель Whisper.
    /// </summary>
    public enum GpuUsage
    {
        /// <summary>Признаков не хватило — ни успех, ни провал GPU не доказаны.</summary>
        Unknown,
        /// <summary>Веса модели загружены на GPU-бэкенд (Vulkan, CUDA и т.п.).</summary>
        Gpu,
        /// <summary>Веса модели загружены на CPU.</summary>
        Cpu
    }

    /// <summary>
    /// Результат определения бэкенда: вердикт + то, на чём он основан (для лога).
    /// </summary>
    public sealed class GpuDetectionResult
    {
        public GpuUsage Usage { get; }

        /// <summary>Имя бэкенда весов модели из нативного лога: «Vulkan0», «CUDA0», «CPU».</summary>
        public string? ModelBackend { get; }

        /// <summary>Имя GPU-устройства, если нативный лог его назвал.</summary>
        public string? DeviceName { get; }

        /// <summary>Короткое объяснение вердикта для лога.</summary>
        public string Evidence { get; }

        /// <summary>
        /// true — встроенная видеокарта с общей памятью (ggml_vulkan «uma: 1»), null — неизвестно.
        /// Такая карта не всегда быстрее процессора; признак показывается пользователю.
        /// </summary>
        public bool? IsIntegrated { get; }

        public GpuDetectionResult(GpuUsage usage, string? modelBackend, string? deviceName, string evidence, bool? isIntegrated = null)
        {
            Usage = usage;
            ModelBackend = modelBackend;
            DeviceName = deviceName;
            Evidence = evidence;
            IsIntegrated = isIntegrated;
        }
    }

    /// <summary>
    /// Определяет, на каком бэкенде реально лежит модель, по нативному логу whisper.cpp.
    ///
    /// Почему не по WhisperFactory.GetRuntimeInfo(): в Whisper.net 1.9.0 эта строка перечисляет
    /// только возможности CPU-бэкенда и не содержит слова «vulkan» даже при работающем GPU —
    /// проверка по ней давала ложный провал и навсегда выключала GPU.
    /// Почему не только по RuntimeOptions.LoadedLibrary: Vulkan-рантайм загружается и при
    /// UseGpu=false или без подходящего устройства, а модель при этом остаётся на CPU.
    ///
    /// Главный признак — строка загрузки весов «whisper_model_load: &lt;бэкенд&gt; total size = …»:
    /// она не зависит от вида GPU-бэкенда (Vulkan, CUDA, Metal) и прямо говорит, где модель.
    /// </summary>
    public static class GpuBackendDetector
    {
        // whisper_model_load:      Vulkan0 total size =   573.40 MB
        private static readonly Regex ModelLoadRegex = new Regex(
            @"whisper_model_load:\s+(?<backend>\S+)\s+total size\s*=", RegexOptions.Compiled);

        // ggml_vulkan: 0 = NVIDIA GeForce GTX 1660 Ti (NVIDIA) | uma: 0 | fp16: 1 | ...
        private static readonly Regex VulkanDeviceRegex = new Regex(
            @"ggml_vulkan:\s*(?<index>\d+)\s*=\s*(?<name>[^|]+?)\s*(\([^)]*\))?\s*\|", RegexOptions.Compiled);

        // «| uma: 1 |» — встроенная карта с общей с процессором памятью
        private static readonly Regex UmaRegex = new Regex(@"\|\s*uma:\s*(?<uma>\d)", RegexOptions.Compiled);

        // Device 0: NVIDIA GeForce RTX 3060, compute capability 8.6, VMM: yes
        private static readonly Regex CudaDeviceRegex = new Regex(
            @"Device\s+(?<index>\d+):\s*(?<name>[^,]+),\s*compute capability", RegexOptions.Compiled);

        /// <summary>
        /// Библиотеки Whisper.net, которые работают только на CPU. Если загружена одна из них,
        /// модель на CPU при любом логе.
        /// </summary>
        private static readonly HashSet<string> CpuOnlyLibraries =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Cpu", "CpuNoAvx" };

        /// <param name="nativeLogLines">Строки нативного лога, пойманные на время создания фабрики и процессора.</param>
        /// <param name="loadedLibrary">RuntimeOptions.LoadedLibrary в строковом виде (null — неизвестно).</param>
        /// <param name="gpuDeviceIndex">Индекс устройства, переданный в WhisperFactoryOptions.GpuDevice.</param>
        public static GpuDetectionResult Detect(IEnumerable<string> nativeLogLines, string? loadedLibrary, int gpuDeviceIndex = 0)
        {
            string? modelBackend = null;
            string? deviceName = null;
            string? firstDeviceName = null;
            bool? isIntegrated = null;
            bool? firstIsIntegrated = null;

            foreach (var raw in nativeLogLines)
            {
                if (string.IsNullOrEmpty(raw)) continue;

                var load = ModelLoadRegex.Match(raw);
                if (load.Success)
                {
                    var backend = load.Groups["backend"].Value;
                    // Если веса разложены по нескольким буферам, достаточно одного не-CPU.
                    if (modelBackend == null || IsCpuBackend(modelBackend))
                        modelBackend = backend;
                    continue;
                }

                var device = VulkanDeviceRegex.Match(raw);
                if (!device.Success) device = CudaDeviceRegex.Match(raw);
                if (device.Success)
                {
                    var name = device.Groups["name"].Value.Trim();
                    var uma = UmaRegex.Match(raw);
                    bool? integrated = uma.Success ? uma.Groups["uma"].Value != "0" : (bool?)null;
                    if (firstDeviceName == null)
                    {
                        firstDeviceName = name;
                        firstIsIntegrated = integrated;
                    }
                    if (int.TryParse(device.Groups["index"].Value, out var index) && index == gpuDeviceIndex)
                    {
                        deviceName = name;
                        isIntegrated = integrated;
                    }
                }
            }

            if (deviceName == null)
            {
                deviceName = firstDeviceName;
                isIntegrated = firstIsIntegrated;
            }

            if (modelBackend != null)
            {
                return IsCpuBackend(modelBackend)
                    ? new GpuDetectionResult(GpuUsage.Cpu, modelBackend, null,
                        $"веса модели на {modelBackend}")
                    : new GpuDetectionResult(GpuUsage.Gpu, modelBackend, deviceName,
                        $"веса модели на {modelBackend}", isIntegrated);
            }

            if (loadedLibrary != null && CpuOnlyLibraries.Contains(loadedLibrary))
            {
                return new GpuDetectionResult(GpuUsage.Cpu, null, null,
                    $"загружен CPU-рантайм {loadedLibrary}, строки загрузки модели в логе нет");
            }

            return new GpuDetectionResult(GpuUsage.Unknown, null, deviceName,
                $"строки загрузки модели в логе нет, рантайм {loadedLibrary ?? "неизвестен"}");
        }

        private static bool IsCpuBackend(string backend) =>
            backend.StartsWith("CPU", StringComparison.OrdinalIgnoreCase);
    }
}
