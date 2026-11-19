using LWhisper.SpeechEngine.Diagnostics;
using System.IO;
using Xunit;

namespace LWhisper.Tests;

[CollectionDefinition("Dictation capture", DisableParallelization = true)]
public class DictationCaptureCollection { }

[Collection("Dictation capture")]
public class LocalDictationCaptureTests
{
    [Fact]
    public void DevCapture_RotatesDirectoriesAndStopsWritingAfterSwitchOff()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            foreach (var folder in new[] { "one", "one", "two" })
            {
                AudioDumpSink.BeginDictation(true, true, Path.Combine(root, folder));
                AudioDumpSink.AppendSessionPcm(new byte[320], 0, 320);
                AudioDumpSink.FlushSession(16000, 1, 16);
                AudioDumpSink.RecordPostFilterText(1, "тест");
            }
            AudioDumpSink.BeginDictation(true, false, root);
            AudioDumpSink.AppendSessionPcm(new byte[320], 0, 320);
            AudioDumpSink.FlushSession(16000, 1, 16);
            Assert.Null(AudioDumpSink.SessionDirectory);
            Assert.Equal(3, Directory.GetFiles(root, "session.wav", SearchOption.AllDirectories).Length);
            Assert.Equal(2, Directory.GetDirectories(Path.Combine(root, "one")).Length);
            foreach (var meta in Directory.GetFiles(root, "meta.jsonl", SearchOption.AllDirectories))
                Assert.Equal(2, File.ReadAllLines(meta).Length);
        }
        finally
        {
            AudioDumpSink.BeginDictation(false, null, null);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void RealSettingsWindow_RespectsBuildAndSavesOnlyEditedChoice()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var original = new LWhisper.Core.Models.AppSettings();
                foreach (var edit in new[] { false, true })
                {
                    var window = new LWhisper.UI.WPF.Views.SettingsWindow(original, new());
                    var panel = (System.Windows.Controls.GroupBox)window.FindName("LocalDictationsPanel");
                    var dev = LWhisper.UI.WPF.Services.UpdateService.IsDevBuild;
                    Assert.Equal(dev ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed, panel.Visibility);
                    if (edit)
                    {
                        window.SaveLocalDictationsCheckBox.IsChecked = true;
                        window.LocalDictationsDirectoryTextBox.Text = Path.GetTempPath();
                    }
                    typeof(LWhisper.UI.WPF.Views.SettingsWindow).GetMethod("SaveButton_Click", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                        .Invoke(window, new object[] { window, new System.Windows.RoutedEventArgs() });
                    Assert.True(window.SettingsAccepted);
                    Assert.Equal(edit && dev ? true : (bool?)null, window.Settings.SaveLocalDictations);
                    Assert.Equal(edit && dev ? Path.GetTempPath() : null, window.Settings.LocalDictationsDirectory);
                    Assert.Null(original.SaveLocalDictations);
                }
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)));
        Assert.Null(failure);
    }

    [Fact]
    public void ReleaseIgnoresSavedDevChoiceAndDevRejectsInvalidDestination()
    {
        var inherited = AudioDumpSink.ParseFlag(Environment.GetEnvironmentVariable("LWHISPER_DEBUG_AUDIO"));
        try
        {
            AudioDumpSink.BeginDictation(false, !inherited, "ignored");
            Assert.Equal(inherited, AudioDumpSink.Enabled);
            Assert.Equal(EnginePaths.DebugRoot, AudioDumpSink.RootDirectory);
            AudioDumpSink.BeginDictation(true, true, "relative");
            Assert.False(AudioDumpSink.Enabled);
            Assert.Null(AudioDumpSink.SessionDirectory);
            AudioDumpSink.BeginDictation(true, true, null);
            Assert.Equal(EnginePaths.DefaultDebugRoot, AudioDumpSink.RootDirectory);
        }
        finally { AudioDumpSink.BeginDictation(false, null, null); }
    }
}
