using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using LWhisper.Core.Models;
using LWhisper.Core.Text;
using LWhisper.UI.WPF.Services;

namespace LWhisper.UI.WPF.Views;

/// <summary>Редактирует локальную копию словаря до явного сохранения.</summary>
public partial class ReplacementDictionaryWindow : Window
{
    private readonly ReplacementDictionaryStore _store;
    private readonly DictionarySnapshot _original;
    private readonly ObservableCollection<EditorRow> _rows;
    private ReplacementDictionary _matcher;

    public DictionarySnapshot? SavedSnapshot { get; private set; }

    public ReplacementDictionaryWindow(ReplacementDictionaryStore store)
    {
        _store = store;
        _original = store.Load();
        _rows = new(_original.Rules.Select(r => new EditorRow(r)));
        _matcher = new(_original.Rules);
        InitializeComponent();
        Rules.ItemsSource = _rows;
        if (_original.State == DictionaryState.Unreadable)
        {
            Status.Text = "Словарь не удалось прочитать. Исправьте или восстановите файл словаря, затем откройте окно заново. Файл не изменён.";
            SaveButton.IsEnabled = false;
        }
        Rules_SelectionChanged(this, null);
    }

    public sealed record EditorRow(ReplacementRule Rule)
    {
        public string Source => Rule.Source;
        public string Target => Rule.Target;
        public string EnabledText => Rule.Enabled ? "Да" : "Нет";
    }

    private void Rules_SelectionChanged(object sender, SelectionChangedEventArgs? e)
    {
        if (SourceText == null || EditButton == null || DeleteButton == null) return;
        var rule = (Rules.SelectedItem as EditorRow)?.Rule;
        SourceText.Text = rule?.Source ?? "";
        TargetText.Text = rule?.Target ?? "";
        EnabledBox.IsChecked = rule?.Enabled ?? true;
        HintText.Text = rule?.Hint ?? "";
        EditButton.IsEnabled = DeleteButton.IsEnabled = rule != null;
    }

    private void Add_Click(object sender, RoutedEventArgs e) => ApplyRow(-1);
    private void Edit_Click(object sender, RoutedEventArgs e) => ApplyRow(Rules.SelectedIndex);

    private void ApplyRow(int index)
    {
        var next = new EditorRow(new ReplacementRule(SourceText.Text.Trim(), TargetText.Text.Trim(),
            EnabledBox.IsChecked == true, index >= 0 ? _rows[index].Rule.Hint : null));
        var draft = _rows.Select(r => r.Rule).ToList();
        if (index < 0) draft.Add(next.Rule); else draft[index] = next.Rule;
        try { _matcher = new ReplacementDictionary(draft); }
        catch (ArgumentException) { Status.Text = "Заполните обе фразы. Исходная фраза должна быть уникальной."; return; }
        if (index < 0) _rows.Add(next); else _rows[index] = next;
        Rules.SelectedItem = next;
        Refresh();
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Rules.SelectedIndex < 0) return;
        _rows.RemoveAt(Rules.SelectedIndex);
        _matcher = new(_rows.Select(r => r.Rule));
        Refresh();
    }

    private void Draft_Changed(object sender, RoutedEventArgs e)
    {
        if (TestOutput != null && TestInput != null) TestOutput.Text = _matcher.Apply(TestInput.Text);
    }

    private void Refresh()
    {
        if (_original.State != DictionaryState.Unreadable) Status.Text = "";
        Draft_Changed(this, new RoutedEventArgs());
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var selected = (Rules.SelectedItem as EditorRow)?.Rule;
        if (SourceText.Text.Trim() != (selected?.Source ?? "") || TargetText.Text.Trim() != (selected?.Target ?? "") ||
            EnabledBox.IsChecked != (selected?.Enabled ?? true))
        {
            Status.Text = "Сначала примените правку строки: «Добавить» или «Изменить выбранное».";
            return;
        }
        try
        {
            SavedSnapshot = _store.Save(_original, _rows.Select(r => r.Rule).ToArray());
            DialogResult = true;
        }
        catch (InvalidOperationException) { Status.Text = "Словарь недоступен или изменился. Откройте его заново."; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { Status.Text = "Не удалось сохранить словарь. Проверьте доступ к файлу и попробуйте снова."; }
    }
}
