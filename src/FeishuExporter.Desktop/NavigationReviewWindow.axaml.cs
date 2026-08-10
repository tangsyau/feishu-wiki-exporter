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
        var likelyCount = _items.Count(item =>
            item.Classification == NavigationPageClassification.LikelyNavigation);
        var uncertainCount = _items.Count - likelyCount;
        CandidateCountText.Text =
            $"高度疑似导航页 {likelyCount} 个，默认勾选；无法确定 {uncertainCount} 个，默认保留。你可以在继续前逐项调整。";
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
        private bool _skip;

        public NavigationReviewItem(NavigationPageCandidate candidate)
        {
            HierarchyToken = candidate.HierarchyToken;
            HierarchyPath = candidate.HierarchyPath;
            Reason = candidate.Reason;
            Classification = candidate.Classification;
            ClassificationLabel = candidate.Classification == NavigationPageClassification.LikelyNavigation
                ? "高度疑似导航页"
                : "无法确定";
            BadgeBackground = candidate.Classification == NavigationPageClassification.LikelyNavigation
                ? "#E8F4ED"
                : "#FFF4DC";
            BadgeForeground = candidate.Classification == NavigationPageClassification.LikelyNavigation
                ? "#28734A"
                : "#8A5A00";
            _skip = candidate.DefaultSkip;
        }

        public string HierarchyToken { get; }
        public string HierarchyPath { get; }
        public string Reason { get; }
        public NavigationPageClassification Classification { get; }
        public string ClassificationLabel { get; }
        public string BadgeBackground { get; }
        public string BadgeForeground { get; }

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
