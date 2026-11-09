using OmegaTube.Data;

namespace OmegaTube.Ui
{
    // What the home screen needs from a feed shown beside the rail (rows by topic or a grid).
    public interface IFeedView
    {
        int Count { get; }
        VideoItem FocusedItem { get; }
        int FocusedRow { get; }   // Glorious: the hero backdrop is shown only on the first row
        Tizen.NUI.BaseComponents.View FocusedCardView { get; }   // the focused card (preview ring), or null
        System.Collections.Generic.List<VideoItem> ItemsAfterFocus(int count);   // next ones (preview prefetch)
        void SetFocused(bool value);
        bool Move(string key);
        void RefreshProgress();
        void Refresh();
        void Wake();   // shown again: resume filling its cards
    }
}
