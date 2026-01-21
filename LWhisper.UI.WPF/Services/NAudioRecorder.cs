using LWhisper.Core.Interfaces;
using LWhisper.Core.Models;
using NAudio.Wave;
using System.IO;

namespace LWhisper.UI.WPF.Services
{
    /// <summary>
    /// Запись аудио через NAudio (Windows)
    /// </summary>
    public class NAudioRecorder : IAudioRecorder
    {
        private WaveInEvent? _waveIn;
        private MemoryStream? _recordedStream;
        private readonly int _sampleRate = 16000;
        private readonly int _channels = 1;
        private readonly int _bitsPerSample = 16;
        private bool _isRecording;

        public bool IsRecording => _isRecording;

        public void StartRecording()
        {
            if (_isRecording) return;

            _isRecording = true;
            _recordedStream = new MemoryStream();

            _waveIn = new WaveInEvent
            {
                WaveFormat = new WaveFormat(_sampleRate, _bitsPerSample, _channels)
            };

            _waveIn.DataAvailable += OnDataAvailable;
            _waveIn.StartRecording();
        }

        public async Task<AudioData> StopRecordingAsync()
        {
            if (_waveIn == null || _recordedStream == null)
            {
                return new AudioData();
            }

            _waveIn.StopRecording();
            _waveIn.DataAvailable -= OnDataAvailable;
            _isRecording = false;

            await Task.Delay(100);

            var audioData = new AudioData
            {
                RawData = _recordedStream.ToArray(),
                SampleRate = _sampleRate,
                Channels = _channels,
                BitsPerSample = _bitsPerSample,
                Duration = TimeSpan.FromSeconds(_recordedStream.Length / (double)(_sampleRate * _channels * _bitsPerSample / 8))
            };

            _waveIn.Dispose();
            _recordedStream.Dispose();
            _waveIn = null;
            _recordedStream = null;

            return audioData;
        }

        public List<string> GetAvailableDevices()
        {
            var devices = new List<string>();
            for (int i = 0; i < WaveInEvent.DeviceCount; i++)
            {
                var capabilities = WaveInEvent.GetCapabilities(i);
                devices.Add(capabilities.ProductName);
            }
            return devices;
        }

        private void OnDataAvailable(object? sender, WaveInEventArgs e)
        {
            _recordedStream?.Write(e.Buffer, 0, e.BytesRecorded);
        }
    }
}

