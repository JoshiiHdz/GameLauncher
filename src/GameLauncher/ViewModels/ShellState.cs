using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameLauncher.Converters;
using GameLauncher.Models;
using GameLauncher.Services;

namespace GameLauncher.ViewModels;

public enum ShellTab { Home, Library }

public enum GuideTab { Views, Launchers, Tools, Drives }

public enum ShellMenuKind { None, Card, More, Drive, Sort }

public enum ShellDirection { Left, Right, Up, Down }

/// <summary>The first item in the Console-ribbon's strip: the tile that stands for the whole library and opens the Library tab.</summary>
public sealed class RibbonLibraryItem
{
    public static RibbonLibraryItem Instance { get; } = new();
}

/// <summary>The last tile in the Console-ribbon's strip when the library has more games than the strip shows: "All N games", which opens the Library tab.</summary>
public sealed class RibbonMoreItem
{
    public static RibbonMoreItem Instance { get; } = new();
}

/// <summary>One of the four cards under the Console-ribbon hero (a label, a value, a line of detail, and what clicking does).</summary>
public sealed partial class ActivityCard(string label, string value, string sub, Action run, bool clickable = true) : ObservableObject
{
    /// <summary>The card does something when clicked (the Game details page's Collections card does; its other facts are plain panels).</summary>
    public bool Clickable { get; private set; } = clickable;

    [ObservableProperty]
    private string _label = label;

    [ObservableProperty]
    private string _value = value;

    [ObservableProperty]
    private string _sub = sub;

    public Action Run { get; private set; } = run;

    /// <summary>Takes another card's text and action, so a card on screen is updated in place rather than rebuilt (rebuilding four cards on every hover was slow).</summary>
    public void CopyFrom(ActivityCard other)
    {
        Label = other.Label;
        Value = other.Value;
        Sub = other.Sub;
        Run = other.Run;
        Clickable = other.Clickable;
        OnPropertyChanged(nameof(Clickable));
    }
}

/// <summary>One row of a theme's menu (card menu, More menu, drive menu). A separator has no action. <see cref="IsActive"/> marks the
/// highlighted row (pointer hover or the arrow keys).</summary>
public sealed partial class ShellMenuItem(string header, Action? run = null, bool destructive = false, bool isSeparator = false) : ObservableObject
{
    public string Header { get; } = header;

    public Action? Run { get; } = run;

    public bool Destructive { get; } = destructive;

    public bool IsSeparator { get; } = isSeparator;

    /// <summary>The row's text is centred (the sort list).</summary>
    public bool Centered { get; init; }

    /// <summary>The row is the option in use now (the sort order that is applied).</summary>
    public bool IsCurrent { get; init; }

    [ObservableProperty]
    private bool _isActive;

    public static ShellMenuItem NewSeparator() => new(string.Empty, null, false, true);
}

/// <summary>View state shared by the themes' shells - which tab is showing, which game has focus, whether the Guide / Control center or a
/// menu is open, and the feedback message. It is view state only (the library and its commands are the library view model's); keeping
/// it out of the domain view models means the same library can wear any theme. Nothing here is saved.</summary>
public sealed partial class ShellState : ObservableObject
{
    private readonly DispatcherTimer _feedbackTimer = new() { Interval = TimeSpan.FromMilliseconds(2600) };
    private bool _wasLoading;

    public ShellState(LibraryViewModel library)
    {
        Library = library;
        FocusedGame = library.FeaturedGame ?? library.Games.FirstOrDefault();
        _feedbackTimer.Tick += (_, _) => ClearFeedback();
        library.PropertyChanged += OnLibraryChanged;
        library.ListsChanged += RefreshRibbon;
        library.ListsChanged += RebuildLibraryGroups;
        ThemeState.Instance.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ThemeState.IsRibbon))
                RebuildLibraryGroups(); // letter headings are a PlayStation-style feature
        };
        RebuildLibraryGroups();
        _wasLoading = library.IsLoading;
        RefreshRibbon();
    }

    public LibraryViewModel Library { get; }

    /// <summary>Home or Library (a filtered grid). Axis has no tabs: there, "Library" simply means a filtered view is showing.</summary>
    [ObservableProperty]
    private ShellTab _tab = ShellTab.Home;

    /// <summary>The game that has focus: the one Enter and M act on, whose art fills the background and whose details fill the hero.
    /// Null while the ribbon's Library tile is the focused item.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeroGame), nameof(HeroEyebrow), nameof(HasHeroGame), nameof(RibbonFocus))]
    private GameEntry? _focusedGame;

    private GameEntry? _watchedGame;

    /// <summary>The cover behind the home: the hero game's art, or none for an icon-fallback game (the plain background then shows).</summary>
    public System.Windows.Media.ImageSource? HeroArt => HeroGame is { } game ? game.Backdrop ?? (game.IsCoverArt ? game.Icon : null) : null;

    /// <summary>Starts fetching the hero game's wide background (the console themes show it full window; the cover alone is a small portrait stretched soft).</summary>
    public void RequestBackdrop()
    {
        if (ThemeState.Instance.IsConsole)
            BackdropArtService.Request(HeroGame);
    }

    partial void OnFocusedGameChanged(GameEntry? value)
    {
        if (_watchedGame is not null)
            _watchedGame.PropertyChanged -= OnWatchedGameChanged;

        _watchedGame = value;
        if (value is not null)
            value.PropertyChanged += OnWatchedGameChanged;

        OnPropertyChanged(nameof(HeroArt));
        RefreshRibbonCards();
        RequestBackdrop();
    }

    partial void OnLibraryTileFocusedChanged(bool value) => RefreshRibbonCards();

    // A cover that arrives (or is changed) after the game took focus must replace the art that was showing.
    private void OnWatchedGameChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(GameEntry.Icon) or nameof(GameEntry.IsCoverArt) or nameof(GameEntry.Backdrop))
            OnPropertyChanged(nameof(HeroArt));

        if (e.PropertyName is nameof(GameEntry.HasPlayTime))
            OnPropertyChanged(nameof(HeroEyebrow));

        if (e.PropertyName is nameof(GameEntry.IsRunning))
            OnPropertyChanged(nameof(RibbonPlayLabel));

        if (e.PropertyName is nameof(GameEntry.Favorite) or nameof(GameEntry.Collections) or nameof(GameEntry.InstallSizeDisplay)
            or nameof(GameEntry.PlayTimeDisplay) or nameof(GameEntry.LastPlayedDisplay))
            RefreshRibbonCards();
    }

    /// <summary>Which shelf (or the grid) the focus is in: 0 is Recently played (or the only row), 1 is All games. A card is focused only if its
    /// row is this one too, because the same game can sit in two shelves.</summary>
    [ObservableProperty]
    private int _focusedRow;

    /// <summary>Console-ribbon only: the first ribbon item (the Library tile) is the focused one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeroGame), nameof(HasHeroGame), nameof(HeroArt), nameof(RibbonFocus))]
    private bool _libraryTileFocused;

    /// <summary>The Guide (Console-tile) or Control center (Console-ribbon) is open.</summary>
    [ObservableProperty]
    private bool _navOpen;

    [ObservableProperty]
    private GuideTab _guideTab = GuideTab.Views;

    [ObservableProperty]
    private ShellMenuKind _menuKind;

    [ObservableProperty]
    private GameEntry? _menuGame;

    [ObservableProperty]
    private DriveSpaceInfo? _menuDrive;

    [ObservableProperty]
    private Rect _menuAnchor;

    [ObservableProperty]
    private int _menuActiveIndex = -1;

    partial void OnMenuActiveIndexChanged(int value)
    {
        for (var i = 0; i < MenuItems.Count; i++)
            MenuItems[i].IsActive = i == value && !MenuItems[i].IsSeparator;
    }

    public ObservableCollection<ShellMenuItem> MenuItems { get; } = new();

    /// <summary>The Console-ribbon strip: the Library tile, then the games (see <see cref="RibbonOrder"/>).</summary>
    public ObservableCollection<object> RibbonItems { get; } = new();

    // ---- The Library grid by letter ---------------------------------------------------------------------------------

    /// <summary>The Library grid's games under their letters (A, B, ...), when the sort is by name; otherwise one group with no heading.</summary>
    public ObservableCollection<LetterGroup> LibraryGroups { get; } = new();

    /// <summary>The letters of the index rail, # then A to Z (all of them, the ones with no games dimmed).</summary>
    public ObservableCollection<LetterItem> LetterIndex { get; } = new();

    /// <summary>The index rail shows only while the grid is sorted by name and there are games to jump between.</summary>
    [ObservableProperty]
    private bool _showLetterIndex;

    /// <summary>Files the library's games under their letters. Run when the games or the sort change.</summary>
    internal void RebuildLibraryGroups()
    {
        var games = Library.Games.ToList();
        var byLetter = ThemeState.Instance.IsRibbon && Library.SortOption is GameSortOption.NameAsc or GameSortOption.NameDesc;
        var groups = new List<LetterGroup>();
        if (byLetter)
        {
            var order = Library.SortOption == GameSortOption.NameAsc ? LibraryLetters.All : LibraryLetters.All.Reverse().ToList();
            var filed = games.GroupBy(g => LibraryLetters.LetterOf(g.Name)).ToDictionary(g => g.Key, g => (IReadOnlyList<GameEntry>)g.ToList());
            groups.AddRange(order.Where(filed.ContainsKey).Select(letter => new LetterGroup(letter, filed[letter], showHeader: true)));
        }
        else if (games.Count > 0)
        {
            groups.Add(new LetterGroup("", games, showHeader: false));
        }

        LibraryGroups.Clear();
        foreach (var group in groups)
            LibraryGroups.Add(group);

        var present = groups.Select(g => g.Letter).ToHashSet();
        LetterIndex.Clear();
        foreach (var letter in LibraryLetters.All)
            LetterIndex.Add(new LetterItem(letter, present.Contains(letter)));

        ShowLetterIndex = byLetter && games.Count > 0;
        SetCurrentLetter(groups.FirstOrDefault()?.Letter);
    }

    /// <summary>Lights the rail's letter for the group the grid is scrolled to.</summary>
    public void SetCurrentLetter(string? letter)
    {
        foreach (var item in LetterIndex)
            item.IsCurrent = letter is not null && item.Letter == letter;
    }

    /// <summary>The cards under the Console-ribbon hero: four facts about the focused game, or four views for the Library tile.</summary>
    public ObservableCollection<ActivityCard> ActivityCards { get; } = new();

    /// <summary>A dialog or the command palette is showing (set by the window); with the page, a menu and the Control center it makes the
    /// Console-ribbon home recede.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOverlayOpen))]
    private bool _modalOpen;

    public bool IsOverlayOpen => ModalOpen || Library.IsPageOpen || IsMenuOpen || NavOpen;

    // ---- Console-ribbon hero text -----------------------------------------------------------------------------------

    public string RibbonCapsule => LibraryTileFocused ? "Library" : HeroGame is { } g ? LauncherText.Name(g.Source) : string.Empty;

    public string RibbonTitle => LibraryTileFocused ? "All games" : HeroGame?.Name ?? string.Empty;

    public string RibbonPlayLabel => LibraryTileFocused ? "Open library" : HeroGame is { IsRunning: true } ? "Running" : "Play";

    public string RibbonMeta
    {
        get
        {
            if (LibraryTileFocused)
                return $"{Library.AllGamesCount} games · {Library.Collections.Count} collections";

            if (HeroGame is not { } g)
                return string.Empty;

            var parts = new List<string>();
            parts.Add(g.HasPlayTime ? $"{g.PlayTimeDisplay} · {g.LastPlayedDisplay}" : "Not played yet");
            parts.Add(DriveAndSize(g));
            return string.Join(" · ", parts.Where(p => p.Length > 0));
        }
    }

    /// <summary>"C: · 14 GB", or "C:" until the size is measured.</summary>
    private static string DriveAndSize(GameEntry g)
    {
        var drive = System.IO.Path.GetPathRoot(g.InstallDir)?.TrimEnd('\\') ?? string.Empty;
        return g.HasInstallSize ? $"{drive} · {g.InstallSizeDisplay}".Trim(' ', '·') : drive;
    }

    /// <summary>Rebuilds the strip and the cards after the library, the focus or the focused game's facts changed.</summary>
    public void RefreshRibbon()
    {
        RefreshRibbonStrip();
        RefreshRibbonCards();
    }

    /// <summary>Re-sorts the strip. Only the library changing needs this; moving focus does not, and sorting every game on each hover made the ribbon lag.</summary>
    private void RefreshRibbonStrip()
    {
        var order = RibbonOrder().Select(g => g is null ? (object)RibbonLibraryItem.Instance : g).ToList();
        if (Library.Games.Count > order.Count - 1)
            order.Add(RibbonMoreItem.Instance); // not every game is on the strip: a last tile leads to all of them
        if (order.SequenceEqual(RibbonItems))
            return;

        RibbonItems.Clear();
        foreach (var item in order)
            RibbonItems.Add(item);
    }

    /// <summary>The line in the pill at the bottom centre: how many games are shown, then what the app last did ("12 of 12 games shown · Ready").</summary>
    public string StatusLine => string.Join(" · ", new[] { Library.FooterCountText, Library.StatusText }.Where(t => !string.IsNullOrWhiteSpace(t)));

    public bool HasStatusLine => StatusLine.Length > 0;

    // The facts line under the title, in pieces so the drive can carry an icon: play time, the drive letter, the size.
    public string RibbonPlayMeta => LibraryTileFocused ? $"{Library.AllGamesCount} games · {Library.Collections.Count} collections"
        : HeroGame is { } g ? (g.HasPlayTime ? $"{g.PlayTimeDisplay} · {g.LastPlayedDisplay}" : "Not played yet") : string.Empty;

    public string RibbonDriveLetter => !LibraryTileFocused && HeroGame is { } g ? System.IO.Path.GetPathRoot(g.InstallDir)?.TrimEnd('\\') ?? string.Empty : string.Empty;

    public bool HasRibbonDrive => RibbonDriveLetter.Length > 0;

    public string RibbonSizeText => !LibraryTileFocused && HeroGame is { HasInstallSize: true } g ? g.InstallSizeDisplay : string.Empty;

    public bool HasRibbonSize => RibbonSizeText.Length > 0;

    /// <summary>The line above the fact cards: the game they describe, or the library.</summary>
    public string ActivityHeading => LibraryTileFocused ? "YOUR LIBRARY" : "ABOUT THIS GAME";

    public bool HasActivityCards => ActivityCards.Count > 0;

    /// <summary>The focused game's play-time figures and sessions, for the home's right-hand Sessions panel; null for the Library tile.</summary>
    [ObservableProperty]
    private PlayTimeViewModel? _heroPlayTime;

    /// <summary>The hero text and the four cards under it, after the focus (or the focused game's facts) changed.</summary>
    private void RefreshRibbonCards()
    {
        var cards = new List<ActivityCard>();
        HeroPlayTime = null;
        if (LibraryTileFocused)
        {
            cards.Add(new("ALL GAMES", $"{Library.AllGamesCount} games", "", () => ShowView("all")));
            cards.Add(new("FAVORITES", $"{Library.FavoritesCount} games", "", () => ShowView("favorites")));
            cards.Add(new("RECENTLY PLAYED", $"{Library.RecentCount} games", "", () => ShowView("recent")));
            cards.Add(new("NOT PLAYED YET", $"{Library.UnplayedCount} games", "", () => ShowView("unplayed")));
        }
        else if (HeroGame is { } g)
        {
            // Exactly the Game details page's fact cards (same wording as GameDetailsViewModel); only Collections is clickable there, so only it is here.
            var details = new GameDetailsViewModel(g, null);
            cards.Add(new("LAUNCHER", details.SourceText, "", () => { }, clickable: false));
            cards.Add(new("INSTALLED IN", g.InstallDir, "", () => { }, clickable: false));
            cards.Add(new("SIZE ON DISK", details.SizeText, "", () => { }, clickable: false));
            HeroPlayTime = Library.BuildPlayTime(g);
            cards.Add(new("PLAY TIME ›", HeroPlayTime.CardText, "", () => Library.ShowPlayTimeCommand.Execute(g)));
            cards.Add(new("ADDED", details.AddedText, "", () => { }, clickable: false));
            cards.Add(new("COLLECTIONS ›", details.CollectionsText, "", () => Library.EditCollectionsCommand.Execute(g)));
            details.Dispose();
        }

        if (ActivityCards.Count == cards.Count)
        {
            for (var i = 0; i < cards.Count; i++)
                ActivityCards[i].CopyFrom(cards[i]);
        }
        else
        {
            ActivityCards.Clear();
            foreach (var card in cards)
                ActivityCards.Add(card);
        }

        OnPropertyChanged(nameof(RibbonCapsule));
        OnPropertyChanged(nameof(RibbonTitle));
        OnPropertyChanged(nameof(RibbonPlayLabel));
        OnPropertyChanged(nameof(RibbonMeta));
        OnPropertyChanged(nameof(RibbonPlayMeta));
        OnPropertyChanged(nameof(RibbonDriveLetter));
        OnPropertyChanged(nameof(HasRibbonDrive));
        OnPropertyChanged(nameof(RibbonSizeText));
        OnPropertyChanged(nameof(HasRibbonSize));
        OnPropertyChanged(nameof(ActivityHeading));
        OnPropertyChanged(nameof(HasActivityCards));
    }

    public bool IsMenuOpen => MenuKind != ShellMenuKind.None;

    /// <summary>The menu's heading: the game's name, or the drive's name.</summary>
    public string MenuHeader => MenuKind == ShellMenuKind.Sort ? "Sort by"
        : MenuKind == ShellMenuKind.Drive && MenuDrive is { } drive ? $"{drive.Label} ({drive.Letter})" : MenuGame?.Name ?? string.Empty;

    /// <summary>The sort list centres its heading and its rows.</summary>
    public bool MenuCentered => MenuKind == ShellMenuKind.Sort;

    public string MenuHeaderUpper => MenuHeader.ToUpperInvariant();

    /// <summary>The line under the heading in the Console-ribbon menu: "Steam - 1.5 h tracked - Today", or "380 GB free" for a drive.</summary>
    public string MenuSubHeader => MenuKind == ShellMenuKind.Drive && MenuDrive is { } drive
        ? $"{ByteFormat.Size(drive.FreeBytes)} free"
        : MenuGame is { } game ? LauncherText.CardMeta(game).Replace(" · ", " - ", StringComparison.Ordinal) : string.Empty;

    [ObservableProperty]
    private string _feedbackText = "";

    [ObservableProperty]
    private string _feedbackDetail = "";

    [ObservableProperty]
    private GameEntry? _feedbackGame;

    [ObservableProperty]
    private bool _hasFeedback;

    /// <summary>The game the hero describes: the focused one, else the library's featured (last played) game.</summary>
    public GameEntry? HeroGame => LibraryTileFocused ? null : FocusedGame ?? Library.FeaturedGame;

    /// <summary>The game the Console-ribbon strip highlights: none while the Library tile is the selected item.</summary>
    public GameEntry? RibbonFocus => LibraryTileFocused ? null : FocusedGame;

    /// <summary>The window is in the small (760 x 480) size; the Console-ribbon hides its activity cards then.</summary>
    [ObservableProperty]
    private bool _isCompact;

    public bool HasHeroGame => HeroGame is not null;

    /// <summary>"JUMP BACK IN" for the most recent game, "RECENTLY PLAYED" for any other played game, else "IN YOUR LIBRARY".</summary>
    public string HeroEyebrow
    {
        get
        {
            var game = HeroGame;
            if (game is null)
                return string.Empty;

            if (game.HasPlayTime && ReferenceEquals(game, MostRecentlyPlayed()))
                return "JUMP BACK IN";

            return game.HasPlayTime ? "RECENTLY PLAYED" : "IN YOUR LIBRARY";
        }
    }

    private GameEntry? MostRecentlyPlayed() =>
        Library.RecentlyPlayedGames.FirstOrDefault() ?? Library.FeaturedGame;

    partial void OnTabChanged(ShellTab value)
    {
        // The grid is one row; on Home the focused game lives in Recently played if it is there, else in All games.
        FocusedRow = value == ShellTab.Library ? 0 : FocusedGame is { } game && Library.RecentlyPlayedGames.Contains(game) ? 0 : 1;

        // Back on Home with a game focused that is not on the strip (a big library keeps the strip short): add it, so the focus has somewhere to be.
        if (value == ShellTab.Home && FocusedGame is { } focused && ThemeState.Instance.IsRibbon && !RibbonItems.Contains(focused))
            RefreshRibbonStrip();
    }

    partial void OnMenuKindChanged(ShellMenuKind value)
    {
        OnPropertyChanged(nameof(IsMenuOpen));
        OnPropertyChanged(nameof(IsOverlayOpen));
        OnPropertyChanged(nameof(MenuCentered));
    }

    partial void OnNavOpenChanged(bool value) => OnPropertyChanged(nameof(IsOverlayOpen));

    // ---- Feedback -------------------------------------------------------------------------------------------------

    /// <summary>Shows a message for 2.6 seconds (replacing any earlier one and restarting the timer): the footer in Axis, a toast in
    /// Console-tile, a capsule in Console-ribbon.</summary>
    public void ShowFeedback(string text, string detail = "", GameEntry? game = null)
    {
        FeedbackText = text;
        FeedbackDetail = detail;
        FeedbackGame = game;
        HasFeedback = true;
        _feedbackTimer.Stop();
        _feedbackTimer.Start();
    }

    public void ClearFeedback()
    {
        _feedbackTimer.Stop();
        HasFeedback = false;
    }

    private void OnLibraryChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(LibraryViewModel.IsLoading):
                if (Library.IsLoading && !_wasLoading)
                    ShowFeedback("Rescanning library");
                else if (!Library.IsLoading && _wasLoading)
                    ShowFeedback("Scan finished", $"{Library.AllGamesCount} games found");

                _wasLoading = Library.IsLoading;
                break;
            case nameof(LibraryViewModel.IsPageOpen):
                OnPropertyChanged(nameof(IsOverlayOpen));
                break;
            case nameof(LibraryViewModel.AllGamesCount):
            case nameof(LibraryViewModel.FavoritesCount):
            case nameof(LibraryViewModel.RecentCount):
            case nameof(LibraryViewModel.UnplayedCount):
                RefreshRibbon();
                break;
            case nameof(LibraryViewModel.FooterCountText):
            case nameof(LibraryViewModel.StatusText):
                OnPropertyChanged(nameof(StatusLine));
                OnPropertyChanged(nameof(HasStatusLine));
                break;
            case nameof(LibraryViewModel.SortOption):
                OnPropertyChanged(nameof(SortLabel));
                RebuildLibraryGroups();
                break;
            case nameof(LibraryViewModel.FeaturedGame):
                RequestBackdrop();
                OnPropertyChanged(nameof(HeroGame));
                OnPropertyChanged(nameof(HeroEyebrow));
                OnPropertyChanged(nameof(HasHeroGame));
                OnPropertyChanged(nameof(HeroArt));
                break;
        }
    }

    // ---- Menus ----------------------------------------------------------------------------------------------------

    private void Open(ShellMenuKind kind, GameEntry? game, DriveSpaceInfo? drive, Rect anchor, IEnumerable<ShellMenuItem> items)
    {
        MenuItems.Clear();
        foreach (var item in items)
            MenuItems.Add(item);

        MenuGame = game;
        MenuDrive = drive;
        MenuAnchor = anchor;
        MenuActiveIndex = -1;
        MenuKind = kind;
        OnPropertyChanged(nameof(MenuHeader));
        OnPropertyChanged(nameof(MenuHeaderUpper));
        OnPropertyChanged(nameof(MenuSubHeader));
    }

    public void CloseMenu()
    {
        if (MenuKind == ShellMenuKind.None)
            return;

        MenuKind = ShellMenuKind.None;
        MenuGame = null;
        MenuDrive = null;
        MenuActiveIndex = -1;
    }

    /// <summary>The rows of a game's menu, in groups split by separators: Play and Game details, then Favorite / Hide / Collections, then the cover rows and Identify, then
    /// Open install location and Uninstall. A menu opened from a screen that already has a button for something leaves that row out, so nothing is offered twice.</summary>
    private List<ShellMenuItem> GameMenuRows(GameEntry game, bool play, bool details, bool openFolder, bool collections = true, bool favorite = true, bool uninstall = true)
    {
        var groups = new List<List<ShellMenuItem>>
        {
            new List<ShellMenuItem>(),
            new List<ShellMenuItem>
            {
                new ShellMenuItem(game.Hidden ? "Unhide" : "Hide", () => ToggleHidden(game)),
            },
            new List<ShellMenuItem>
            {
                new ShellMenuItem("Change Cover Manually", () => Library.ChangeCoverCommand.Execute(game)),
                new ShellMenuItem("Reset cover to automatic", () => Library.ResetCoverCommand.Execute(game)),
                new ShellMenuItem("Identify game...", () => Library.IdentifyGameCommand.Execute(game)),
            },
            new List<ShellMenuItem>(),
        };
        if (favorite)
            groups[1].Insert(0, new ShellMenuItem(game.Favorite ? "Unfavorite" : "Favorite", () => ToggleFavorite(game)));

        if (collections)
            groups[1].Add(new ShellMenuItem("Collections...", () => Library.EditCollectionsCommand.Execute(game)));

        if (play)
            groups[0].Add(new ShellMenuItem("Play", () => Play(game)));

        if (details)
            groups[0].Add(new ShellMenuItem("Game details...", () => Library.ShowGameDetailsCommand.Execute(game)));

        if (openFolder)
            groups[3].Add(new ShellMenuItem("Open install location", () => Library.OpenInstallLocationCommand.Execute(game)));

        if (uninstall)
            groups[3].Add(new ShellMenuItem("Uninstall...", () => Library.UninstallGameCommand.Execute(game), destructive: true));

        var rows = new List<ShellMenuItem>();
        foreach (var group in groups.Where(g => g.Count > 0))
        {
            if (rows.Count > 0)
                rows.Add(ShellMenuItem.NewSeparator());

            rows.AddRange(group);
        }

        return rows;
    }

    /// <summary>The PlayStation-style home shows a game's details and has Play, Uninstall, More and a Favorite star as buttons (and Collections as a fact card),
    /// so its menus do not repeat them. Open install location is only in the menus there.</summary>
    private bool DetailsAreOnScreen => ThemeState.Instance.IsRibbon && Tab == ShellTab.Home && !Library.IsPageOpen;

    /// <summary>The card menu (right click, M): everything you can do to a game, without the rows the screen already has as buttons.</summary>
    public void OpenCardMenu(GameEntry game, Rect anchor)
    {
        FocusedGame = game;
        var onHome = DetailsAreOnScreen;
        Open(ShellMenuKind.Card, game, null, anchor, GameMenuRows(game, play: true, details: !onHome, openFolder: true, collections: !onHome, favorite: !onHome, uninstall: !onHome));
    }

    /// <summary>The More menu on a hero and on Game details: the same rows minus Play and Game details (the hero or page has those) and, in the PlayStation style,
    /// minus Collections (a fact card there) and, on the PlayStation hero, Favorite and Uninstall (buttons there). On the PlayStation Game details page the
    /// menu is as it was.</summary>
    public void OpenMoreMenu(GameEntry game, Rect anchor, bool onDetailsPage = false)
    {
        var ribbon = ThemeState.Instance.IsRibbon;
        var onHero = ribbon && !onDetailsPage;
        Open(ShellMenuKind.More, game, null, anchor,
            GameMenuRows(game, play: false, details: false, openFolder: onHero || !ribbon, collections: !ribbon, favorite: !onHero, uninstall: !onHero));
    }

    /// <summary>The drive menu (Guide and Control center): what is using the drive, and stop or resume searching it.</summary>
    public void OpenDriveMenu(DriveSpaceInfo drive, Rect anchor) =>
        Open(ShellMenuKind.Drive, null, drive, anchor,
        [
            new("What's using this drive?", () => Library.ShowStorageCommand.Execute(drive.Letter)),
            new(drive.IsIgnored ? "Search this drive again" : "Stop searching this drive", () => ToggleDriveSearch(drive), destructive: !drive.IsIgnored),
        ]);

    /// <summary>The sort list under the Sort chip: every sort order, the one in use marked, text centred.</summary>
    public void OpenSortMenu(Rect anchor)
    {
        var items = Library.SortOptions
            .Select(o => new ShellMenuItem(o.Label, () => Library.SortOption = o.Value) { Centered = true, IsCurrent = Equals(o.Value, Library.SortOption) })
            .ToList();
        Open(ShellMenuKind.Sort, null, null, anchor, items);
        MenuActiveIndex = Math.Max(0, items.FindIndex(i => i.IsCurrent));
    }

    /// <summary>Moves the highlighted menu row by <paramref name="step"/> (wrapping), skipping separators.</summary>
    public void MoveMenuSelection(int step)
    {
        var rows = MenuItems.Select((item, index) => (item, index)).Where(t => !t.item.IsSeparator).Select(t => t.index).ToList();
        if (rows.Count == 0)
            return;

        var at = rows.IndexOf(MenuActiveIndex);
        at = at < 0 ? (step > 0 ? 0 : rows.Count - 1) : (at + step + rows.Count) % rows.Count;
        MenuActiveIndex = rows[at];
    }

    /// <summary>Closes the menu and runs the row (a separator does nothing).</summary>
    public void RunMenuItem(ShellMenuItem item)
    {
        if (item.IsSeparator || item.Run is null)
            return;

        CloseMenu();
        item.Run();
    }

    public void RunActiveMenuItem()
    {
        if (MenuActiveIndex >= 0 && MenuActiveIndex < MenuItems.Count)
            RunMenuItem(MenuItems[MenuActiveIndex]);
    }

    // ---- Actions that report back -------------------------------------------------------------------------------------

    [RelayCommand]
    public void Play(GameEntry? game)
    {
        if (game is null)
            return;

        if (game.IsRunning)
        {
            ShowFeedback($"{game.Name} is already running", "", game);
            return;
        }

        Library.LaunchCommand.Execute(game);
        ShowFeedback($"Starting {game.Name}", $"via {LauncherText.Name(game.Source)}", game);
    }

    public void ToggleFavorite(GameEntry game)
    {
        var wasFavorite = game.Favorite;
        Library.ToggleFavoriteCommand.Execute(game);
        ShowFeedback(wasFavorite ? "Removed from favorites" : "Added to favorites", game.Name, game);
    }

    public void ToggleHidden(GameEntry game)
    {
        var wasHidden = game.Hidden;
        Library.ToggleHiddenCommand.Execute(game);
        ShowFeedback(wasHidden ? "Shown in library" : "Hidden from library", wasHidden ? game.Name : "Settings > Library lists hidden games", game);
    }

    public void ToggleDriveSearch(DriveSpaceInfo drive)
    {
        var wasIgnored = drive.IsIgnored;
        Library.ToggleDriveIgnoredCommand.Execute(drive.Letter);
        ShowFeedback(wasIgnored ? $"Searching {drive.Label} ({drive.Letter}) again" : $"Stopped searching {drive.Label} ({drive.Letter})",
            wasIgnored ? string.Empty : "Turn it back on in Settings > Library");
    }

    // ---- Navigation commands the shells bind to ---------------------------------------------------------------------

    /// <summary>Home: the unfiltered library (Recently played, then everything).</summary>
    [RelayCommand]
    private void ShowHome()
    {
        CloseMenu();
        NavOpen = false;
        Library.SearchText = string.Empty;
        Library.SelectViewCommand.Execute("all"); // also closes an open page and clears the drive and collection narrowing
        Tab = ShellTab.Home;
    }

    /// <summary>The Library tab: the filtered grid for whichever view is selected.</summary>
    [RelayCommand]
    private void ShowLibrary()
    {
        CloseMenu();
        NavOpen = false;
        Library.ClosePageCommand.Execute(null);
        Tab = ShellTab.Library;
    }

    /// <summary>Filters the library to one drive (Guide and Control center drive cards) and shows it in the Library tab.</summary>
    [RelayCommand]
    private void ShowDrive(string? letter)
    {
        CloseMenu();
        NavOpen = false;
        Library.SelectDriveCommand.Execute(letter);
        Tab = ShellTab.Library;
    }

    /// <summary>Opens a view (all, favorites, recent, unplayed, duplicates) in the Library tab and closes the Guide / Control center.</summary>
    [RelayCommand]
    private void ShowView(string? view)
    {
        CloseMenu();
        NavOpen = false;
        Library.SelectViewCommand.Execute(view);
        Tab = ShellTab.Library;
    }

    [RelayCommand]
    private void ShowCollection(string? name)
    {
        CloseMenu();
        NavOpen = false;
        Library.SelectCollectionCommand.Execute(name);
        Tab = ShellTab.Library;
    }

    [RelayCommand]
    private void ToggleNav()
    {
        CloseMenu();
        NavOpen = !NavOpen;
    }

    [RelayCommand]
    private void CloseNav() => NavOpen = false;

    [RelayCommand]
    private void SetGuideTab(string? tab)
    {
        if (Enum.TryParse<GuideTab>(tab, ignoreCase: true, out var parsed))
            GuideTab = parsed;
    }

    /// <summary>Runs a Guide / Control center action (a page, a rescan, Add folder...) and closes the sheet first.</summary>
    [RelayCommand]
    private void RunFromNav(System.Windows.Input.ICommand? command)
    {
        NavOpen = false;
        if (command?.CanExecute(null) == true)
            command.Execute(null);
    }

    /// <summary>The next sort order, wrapping around: what the Sort chip does when clicked.</summary>
    [RelayCommand]
    private void CycleSort()
    {
        var options = Library.SortOptions;
        if (options.Count == 0)
            return;

        var at = 0;
        for (var i = 0; i < options.Count; i++)
        {
            if (Equals(options[i].Value, Library.SortOption))
                at = i;
        }

        Library.SortOption = options[(at + 1) % options.Count].Value;
    }

    public string SortLabel => Library.SortOptions.FirstOrDefault(o => Equals(o.Value, Library.SortOption))?.Label ?? string.Empty;

    // ---- Moving focus with the arrow keys (spec 07 section 7.2) --------------------------------------------------------

    /// <summary>How many tiles fit across the Library grid; the view sets it from the laid-out grid so Up and Down move by a line.</summary>
    public int GridColumns { get; set; } = 1;

    /// <summary>Moves the focus one step. Console-tile home: Left and Right move along the shelf, Up and Down switch shelf keeping the
    /// position. Library tab: Left and Right move by one, Up and Down by a whole line. Console-ribbon home: one row, Left and Right only.
    /// Returns false when there was nothing to move (so the key can do its normal thing).</summary>
    public bool MoveFocus(ShellDirection direction)
    {
        var ribbon = ThemeManager.Current == ThemeId.ConsoleRibbon;
        if (Tab == ShellTab.Library)
            return MoveInGrid(direction);

        return ribbon ? MoveInRibbon(direction) : MoveInShelves(direction);
    }

    private bool MoveInShelves(ShellDirection direction)
    {
        var recent = Library.RecentlyPlayedGames;
        var all = Library.Games;
        if (all.Count == 0)
            return false;

        var row = FocusedRow == 0 && recent.Count > 0 ? 0 : 1;
        IList<GameEntry> list = row == 0 ? recent : all;
        var index = FocusedGame is { } game ? Math.Max(0, list.IndexOf(game)) : 0;

        switch (direction)
        {
            case ShellDirection.Left:
                index = Math.Max(0, index - 1);
                break;
            case ShellDirection.Right:
                index = Math.Min(list.Count - 1, index + 1);
                break;
            case ShellDirection.Down when row == 0:
                row = 1;
                list = all;
                index = Math.Min(index, all.Count - 1);
                break;
            case ShellDirection.Up when row == 1 && recent.Count > 0:
                row = 0;
                list = recent;
                index = Math.Min(index, recent.Count - 1);
                break;
            default:
                return false;
        }

        FocusedRow = row;
        FocusedGame = list[index];
        return true;
    }

    /// <summary>The Library grid as rows: each letter's games fill rows of <see cref="GridColumns"/>, and a new letter starts a new row (as it is drawn).</summary>
    private List<List<GameEntry>> GridRows()
    {
        var columns = Math.Max(1, GridColumns);
        return LibraryGroups.SelectMany(group => group.Games.Chunk(columns).Select(chunk => chunk.ToList())).ToList();
    }

    private bool MoveInGrid(ShellDirection direction)
    {
        var rows = GridRows();
        if (rows.Count == 0)
            return false;

        var row = 0;
        var column = 0;
        if (FocusedGame is { } game)
        {
            row = Math.Max(0, rows.FindIndex(r => r.Contains(game)));
            column = Math.Max(0, rows[row].IndexOf(game));
        }

        switch (direction)
        {
            case ShellDirection.Left:
                if (column > 0) column--;
                else if (row > 0) { row--; column = rows[row].Count - 1; }
                else return false;
                break;
            case ShellDirection.Right:
                if (column < rows[row].Count - 1) column++;
                else if (row < rows.Count - 1) { row++; column = 0; }
                else return false;
                break;
            case ShellDirection.Up:
                if (row == 0) return false;
                row--;
                column = Math.Min(column, rows[row].Count - 1);
                break;
            default:
                if (row == rows.Count - 1) return false;
                row++;
                column = Math.Min(column, rows[row].Count - 1);
                break;
        }

        FocusedRow = 0;
        FocusedGame = rows[row][column];
        return true;
    }

    /// <summary>The most games the ribbon strip shows. Past that a long library would mean scrolling the whole strip to find anything: the strip keeps what is
    /// likely wanted (what was played lately, the favorites, what was added last) and a last tile leads to everything; the Library tab has an A to Z index.</summary>
    internal const int MaxRibbonGames = 14;

    /// <summary>How many of those are taken from the recently played games before favorites and new additions fill the rest.</summary>
    private const int MaxRibbonRecent = 10;

    /// <summary>The ribbon's items in order: the Library tile (null), then the games. A small library shows every game (played lately first, then A to Z); a big one shows
    /// <see cref="MaxRibbonGames"/>: the recently played, then favorites, then the newest additions - plus the focused game, so a game picked from the palette is on the strip.</summary>
    public IReadOnlyList<GameEntry?> RibbonOrder()
    {
        var order = new List<GameEntry?> { null };
        var all = Library.Games.ToList();
        var recent = Library.RecentlyPlayedGames.ToList();
        var byName = StringComparer.OrdinalIgnoreCase;
        if (all.Count <= MaxRibbonGames)
        {
            order.AddRange(recent);
            order.AddRange(all.Where(g => !recent.Contains(g)).OrderBy(g => g.Name, byName));
            return order;
        }

        var chosen = new List<GameEntry>(recent.Take(MaxRibbonRecent));
        chosen.AddRange(all.Where(g => g.Favorite && !chosen.Contains(g)).OrderBy(g => g.Name, byName).Take(MaxRibbonGames - chosen.Count));
        chosen.AddRange(all.Where(g => !chosen.Contains(g)).OrderByDescending(g => g.DateAdded).ThenBy(g => g.Name, byName).Take(MaxRibbonGames - chosen.Count));
        if (FocusedGame is { } focused && all.Contains(focused) && !chosen.Contains(focused))
            chosen.Insert(0, focused);

        order.AddRange(chosen);
        return order;
    }

    private bool MoveInRibbon(ShellDirection direction)
    {
        if (direction is ShellDirection.Up or ShellDirection.Down)
            return false;

        var order = RibbonOrder();
        var index = LibraryTileFocused ? 0 : Math.Max(0, order.ToList().IndexOf(FocusedGame));
        var target = Math.Clamp(index + (direction == ShellDirection.Right ? 1 : -1), 0, order.Count - 1);
        if (target == index && !(LibraryTileFocused && index == 0 && FocusedGame is null))
            return false;

        FocusRibbonItem(order[target]);
        return true;
    }

    /// <summary>Focuses a ribbon item: a game, or null for the Library tile.</summary>
    public void FocusRibbonItem(GameEntry? game)
    {
        LibraryTileFocused = game is null;
        if (game is not null)
            FocusedGame = game;

        // A game that is not on the strip (picked from the palette, or from the Library tab) is added to it, so the focus has somewhere to be.
        if (game is not null && ThemeState.Instance.IsRibbon && !RibbonItems.Contains(game))
            RefreshRibbonStrip();
    }

    /// <summary>The PlayStation-style home asks its Play button to take the keyboard focus (a game was picked; Enter now plays it).</summary>
    public event EventHandler? PlayFocusRequested;

    public void RequestPlayFocus() => PlayFocusRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Brings a game up on the home screen: closes any page, goes Home, focuses the game's icon and highlights Play.</summary>
    public void ShowGameOnHome(GameEntry game)
    {
        Library.ClosePageCommand.Execute(null);
        if (Tab != ShellTab.Home)
            ShowHomeCommand.Execute(null);

        FocusRibbonItem(game);
        RequestPlayFocus();
    }

    /// <summary>What Enter (or a click on the focused item) does: the Xbox style opens Game details for a game; the PlayStation style, whose home already shows the details,
    /// sends the focus to the Play button (but a game in its Library tab still opens Game details). The Library tile opens the Library tab.</summary>
    public void OpenFocused()
    {
        if (LibraryTileFocused)
        {
            ShowLibrary();
            return;
        }

        if (FocusedGame is not { } game)
            return;

        // PlayStation style: on the home screen the details are already showing, so the focus goes to Play; in the Library tab, which has no details panel,
        // a game opens its Game details page.
        if (ThemeState.Instance.IsRibbon && Tab != ShellTab.Library)
        {
            RequestPlayFocus();
            return;
        }

        Library.ShowGameDetailsCommand.Execute(game);
    }
}
