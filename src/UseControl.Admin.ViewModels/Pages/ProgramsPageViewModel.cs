using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UseControl.Admin.ViewModels.Infrastructure;
using UseControl.Core.Configuration;
using UseControl.Localization;

namespace UseControl.Admin.ViewModels.Pages;

/// <summary>A row of one of the program lists. <see cref="Key"/> is what removing it needs (path, name or SHA-256).</summary>
public sealed record ProgramRow(string Key, string Text, string Category, string? Note);

public sealed record Choice<T>(T Value, string Text);

public sealed partial class ProgramsPageViewModel : PageViewModel
{
    private readonly Func<string, RuleCategory, FileHashEntry> _fingerprint;

    public ProgramsPageViewModel(PanelSession session, Func<string, RuleCategory, FileHashEntry> fingerprint)
        : base(session, "Ui.Nav.Programs", "")
    {
        _fingerprint = fingerprint;
        BuildCategories();
    }

    public ObservableCollection<ProgramRow> BlockedFolders { get; } = [];
    public ObservableCollection<ProgramRow> AllowedFolders { get; } = [];
    public ObservableCollection<ProgramRow> BlockedNames { get; } = [];
    public ObservableCollection<ProgramRow> BlockedFiles { get; } = [];

    public ObservableCollection<Choice<RuleCategory>> Categories { get; } = [];

    [ObservableProperty]
    private Choice<RuleCategory>? _category;

    [ObservableProperty]
    private string _newName = "";

    private RuleCategory SelectedCategory => Category?.Value ?? RuleCategory.Games;

    [RelayCommand]
    private Task BlockFolder()
    {
        var path = Session.Dialogs.PickFolder(Loc.T("Ui.Programs.PickBlockFolder"));
        var cat = SelectedCategory;
        return path is null ? Task.CompletedTask : Session.EditAsync(c => ConfigEditor.AddBlockedPath(c, path, cat, Session.Paths));
    }

    [RelayCommand]
    private Task AllowFolder()
    {
        var path = Session.Dialogs.PickFolder(Loc.T("Ui.Programs.PickAllowFolder"));
        return path is null ? Task.CompletedTask : Session.EditAsync(c => ConfigEditor.AddAllowedPath(c, path));
    }

    [RelayCommand]
    private async Task BlockName()
    {
        var name = NewName;
        var cat = SelectedCategory;
        if (await Session.EditAsync(c => ConfigEditor.AddBlockedName(c, name, cat)).ConfigureAwait(true)) NewName = "";
    }

    [RelayCommand]
    private Task BlockFile()
    {
        var path = Session.Dialogs.PickFile(Loc.T("Ui.Programs.PickBlockFile"), Loc.T("Ui.Programs.ProgramsFilter") + "|*.exe;*.com;*.bat;*.cmd;*.msi|*.*|*.*");
        if (path is null) return Task.CompletedTask;
        var cat = SelectedCategory;
        return Session.RunAsync(async () =>
        {
            var entry = _fingerprint(path, cat);
            await Session.EditAsync(c => ConfigEditor.AddBlockedFile(c, entry)).ConfigureAwait(true);
        });
    }

    [RelayCommand]
    private Task RemoveBlockedFolder(ProgramRow? row) => row is null ? Task.CompletedTask : Session.EditAsync(c => ConfigEditor.RemoveBlockedPath(c, row.Key));

    [RelayCommand]
    private Task RemoveAllowedFolder(ProgramRow? row) => row is null ? Task.CompletedTask : Session.EditAsync(c => ConfigEditor.RemoveAllowedPath(c, row.Key));

    [RelayCommand]
    private Task RemoveBlockedName(ProgramRow? row) => row is null ? Task.CompletedTask : Session.EditAsync(c => ConfigEditor.RemoveBlockedName(c, row.Key));

    [RelayCommand]
    private Task RemoveBlockedFile(ProgramRow? row) => row is null ? Task.CompletedTask : Session.EditAsync(c => ConfigEditor.RemoveBlockedFile(c, row.Key));

    public static string CategoryText(RuleCategory c) => Loc.T("Ui.Category." + c);

    private void BuildCategories()
    {
        var selected = SelectedCategory;
        Replace(Categories, new[] { RuleCategory.Games, RuleCategory.Vpn, RuleCategory.Tools, RuleCategory.Other }.Select(c => new Choice<RuleCategory>(c, CategoryText(c))));
        Category = Categories.First(c => c.Value == selected);
    }

    protected override void Rebuild()
    {
        BuildCategories();
        var c = Session.Config;
        if (c is null) return;
        Replace(BlockedFolders, c.BlockedPaths.Select(e => new ProgramRow(e.Path, e.Path, CategoryText(e.Category), e.Note)));
        Replace(AllowedFolders, c.AllowedPaths.Select(e => new ProgramRow(e.Path, e.Path, "", e.Note)));
        Replace(BlockedNames, c.BlockedNames.Select(e => new ProgramRow(e.Name, e.Name, CategoryText(e.Category), e.Note)));
        Replace(BlockedFiles, c.BlockedFiles.Select(e => new ProgramRow(e.Sha256, e.FileName, CategoryText(e.Category), e.Note ?? e.Sha256[..16] + "…")));
    }
}
