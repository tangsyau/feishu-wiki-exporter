using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Interactivity;
using FeishuExporter.Core;

namespace FeishuExporter.Desktop;

public partial class NavigationReviewWindow : Window
{
    private readonly List<NavigationReviewItem> _items = [];

    public NavigationReviewWindow()
    {
        InitializeComponent();
    }

    public NavigationReviewWindow(IReadOnlyList<NavigationPageCandidate> candidates)
        : this()
    {
        _items.AddRange(candidates.Select(candidate => new NavigationReviewItem(candidate)));
        foreach (var item in _items)
        {
            item.PropertyChanged += ReviewItem_PropertyChanged;
        }
        CandidateList.ItemsSource = _items;
        CandidateCountText.Text = $"程序发现 {_items.Count} 个疑似导航页，默认全部勾选。你可以在继续前逐项调整。";
        UpdateSelectedCount();
    }

    private void SelectAll_Click(object? sender, RoutedEventArgs e)
    {
        foreach (var item in _items)
        {
            item.Skip = true;
        }
    }

    private void ClearAll_Click(object? sender, RoutedEventArgs e)
    {
        foreach (var item in _items)
        {
            item.Skip = false;
        }
    }

    private void Continue_Click(object? sender, RoutedEventArgs e)
    {
        var selected = _items
            .Where(item => item.Skip)
            .Select(item => item.HierarchyToken)
            .ToHashSet(StringComparer.Ordinal);
        Close(selected);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(null);

    private void ReviewItem_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.Equals(e.PropertyName, nameof(NavigationReviewItem.Skip), StringComparison.Ordinal))
        {
            UpdateSelectedCount();
        }
    }

    private void UpdateSelectedCount()
    {
        SelectedCountText.Text = $"将跳过 {_items.Count(item => item.Skip)} 个文档";
    }

    private sealed class NavigationReviewItem : INotifyPropertyChanged
    {
        private bool _skip = true;

        public NavigationReviewItem(NavigationPageCandidate candidate)
        {
            HierarchyToken = candidate.HierarchyToken;
            HierarchyPath = candidate.HierarchyPath;
            Reason = candidate.Reason;
        }

        public string HierarchyToken { get; }
        public string HierarchyPath { get; }
        public string Reason { get; }

        public bool Skip
        {
            get => _skip;
            set
            {
                if (_skip == value)
                {
                    return;
                }
                _skip = value;
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
