namespace DontStarve.Config;

internal sealed class ModConfig
{
    private bool enableDawnDuskMusic = true;

    public bool EnableDawnDuskMusic
    {
        get => enableDawnDuskMusic;
        set
        {
            enableDawnDuskMusic = value;
            IsEnableDawnDuskMusicDirty = true;
        }
    }

    internal bool IsEnableDawnDuskMusicDirty { get; private set; }

    internal void LoadEnableDawnDuskMusic(bool value)
    {
        enableDawnDuskMusic = value;
        IsEnableDawnDuskMusicDirty = false;
    }

    internal void MarkSaved()
    {
        IsEnableDawnDuskMusicDirty = false;
    }
}
