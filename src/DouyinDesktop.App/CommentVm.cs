using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using DouyinDesktop.Core.Models;

namespace DouyinDesktop.App;

/// <summary>
/// 评论列表里的一条。包一层是为了支持"内联展开回复"——
/// 直接绑 Comment 模型的话，展开状态和子列表没地方放。
/// </summary>
public sealed class CommentVm : INotifyPropertyChanged
{
    private bool _expanded;
    private bool _loading;

    public CommentVm(Comment model) => Model = model;

    public Comment Model { get; }

    public ObservableCollection<CommentVm> Replies { get; } = new();

    public string Nickname => Model.Nickname;
    public string Text => Model.Text;
    public string MetaText => Model.MetaText;
    public string LikeText => Model.LikeText;

    public Visibility ExpandVisibility
        => Model.HasReplies ? Visibility.Visible : Visibility.Collapsed;

    public string ExpandText => _loading
        ? "加载中…"
        : _expanded ? "收起回复" : $"展开 {Model.ReplyCount} 条回复";

    public Visibility RepliesVisibility
        => _expanded ? Visibility.Visible : Visibility.Collapsed;

    public bool IsExpanded => _expanded;

    public void SetLoading(bool loading)
    {
        _loading = loading;
        Raise(nameof(ExpandText));
    }

    public void Expand(IEnumerable<Comment> replies)
    {
        Replies.Clear();
        foreach (var r in replies) Replies.Add(new CommentVm(r));
        _expanded = true;
        _loading = false;

        Raise(nameof(ExpandText));
        Raise(nameof(RepliesVisibility));
    }

    public void Collapse()
    {
        _expanded = false;
        Raise(nameof(ExpandText));
        Raise(nameof(RepliesVisibility));
    }

    private void Raise(string name)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public event PropertyChangedEventHandler? PropertyChanged;
}
