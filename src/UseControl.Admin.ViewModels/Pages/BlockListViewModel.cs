using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UseControl.Admin.ViewModels.Infrastructure;
using UseControl.Core;
using UseControl.Core.Configuration;
using UseControl.Localization;

namespace UseControl.Admin.ViewModels.Pages;

public enum BlockKind
{
    /// <summary>A program file: blocked by its fingerprint and by its name.</summary>
    Program,

    /// <summary>Only a program file name.</summary>
    Name,

    /// <summary>Everything in a folder.</summary>
    Folder,
}

/// <summary>A row of a simple folder list (allowed folders).</summary>
public sealed record ProgramRow(string Key, string Text, string Category, string? Note);

/// <summary>One line of a block list. <see cref="Key"/> is what removing it needs (SHA-256, name or path).</summary>
public sealed record BlockRow(BlockKind Kind, string Key, string Text, string KindText, string CategoryText, string? Detail);

/// <summary>
/// A list of blocked programs, names and folders of some categories, with the actions to add and remove
/// them. The Games page and the Programs page each have one; the categories are chosen by the page,
/// not by the administrator.
/// </summary>
public sealed partial class BlockListViewModel : ObservableObject
{
    private readonly PanelSession _session;
    private readonly Func<RuleCategory, bool> _shows;
    private readonly RuleCategory _addAs;
    private readonly Func<string, RuleCategory, FileHashEntry> _fingerprint;
    private readonly string _pickProgramKey;
    private readonly string _pickFolderKey;

    public BlockListViewModel(PanelSession session, Func<RuleCategory, bool> shows, RuleCategory addAs,
        Func<string, RuleCategory, FileHashEntry> fingerprint, string pickProgramKey, string pickFolderKey)
    {
        _session = session;
        _shows = shows;
        _addAs = addAs;
        _fingerprint = fingerprint;
        _pickProgramKey = pickProgramKey;
        _pickFolderKey = pickFolderKey;
    }

    public ObservableCollection<BlockRow> Rows { get; } = [];

    /// <summary>Button texts differ per page ("Add a game…" / "Block a program…").</summary>
    public string AddProgramText => Loc.T(_addAs == RuleCategory.Games ? "Ui.Games.AddGame" : "Ui.Block.AddProgram");

    public string AddFolderText => Loc.T(_addAs == RuleCategory.Games ? "Ui.Games.AddGameFolder" : "Ui.Block.AddFolder");

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddNameCommand))]
    private string _newName = "";

    private bool HasName() => !string.IsNullOrWhiteSpace(NewName);

    /// <summary>Pick a program file: it is blocked by its fingerprint (renaming or moving does not help) and by its name (new versions too).</summary>
    [RelayCommand]
    private Task AddProgram()
    {
        var path = _session.Dialogs.PickFile(Loc.T(_pickProgramKey), Loc.T("Ui.Programs.ProgramsFilter") + "|*.exe;*.com;*.bat;*.cmd;*.msi");
        if (path is null) return Task.CompletedTask;
        return _session.RunAsync(async () =>
        {
            var entry = _fingerprint(path, _addAs);
            await _session.EditPresetAsync(c =>
            {
                ConfigEditor.AddBlockedFile(c, entry);
                if (!c.BlockedNames.Any(n => n.Name.Equals(entry.FileName, StringComparison.OrdinalIgnoreCase)))
                    ConfigEditor.AddBlockedName(c, entry.FileName, _addAs);
            }).ConfigureAwait(true);
        });
    }

    [RelayCommand]
    private Task AddFolder()
    {
        var path = _session.Dialogs.PickFolder(Loc.T(_pickFolderKey));
        return path is null ? Task.CompletedTask : _session.EditPresetAsync(c => ConfigEditor.AddBlockedPath(c, path, _addAs, _session.Paths));
    }

    [RelayCommand(CanExecute = nameof(HasName))]
    private async Task AddName()
    {
        var name = NewName;
        if (await _session.EditPresetAsync(c => ConfigEditor.AddBlockedName(c, name, _addAs)).ConfigureAwait(true)) NewName = "";
    }

    [RelayCommand]
    private Task Remove(BlockRow? row)
    {
        if (row is null) return Task.CompletedTask;
        return _session.EditPresetAsync(c =>
        {
            switch (row.Kind)
            {
                case BlockKind.Folder:
                    ConfigEditor.RemoveBlockedPath(c, row.Key);
                    break;
                case BlockKind.Name:
                    ConfigEditor.RemoveBlockedName(c, row.Key);
                    break;
                case BlockKind.Program:
                    var file = c.BlockedFiles.FirstOrDefault(f => f.Sha256.Equals(row.Key, StringComparison.OrdinalIgnoreCase));
                    ConfigEditor.RemoveBlockedFile(c, row.Key);
                    // The name goes too, unless another blocked file has the same name.
                    if (file is not null && !c.BlockedFiles.Any(f => f.FileName.Equals(file.FileName, StringComparison.OrdinalIgnoreCase)))
                    {
                        try { ConfigEditor.RemoveBlockedName(c, file.FileName); }
                        catch (UcException) { }
                    }
                    break;
            }
        });
    }

    public void Rebuild(Preset? c)
    {
        OnPropertyChanged(nameof(AddProgramText));
        OnPropertyChanged(nameof(AddFolderText));
        Rows.Clear();
        if (c is null) return;
        var files = c.BlockedFiles.Where(f => _shows(f.Category)).ToList();
        foreach (var f in files.OrderBy(f => f.FileName, StringComparer.OrdinalIgnoreCase))
        {
            Rows.Add(new BlockRow(BlockKind.Program, f.Sha256, f.FileName, Loc.T("Ui.Block.Kind.Program"), Category(f.Category), f.Note));
        }
        foreach (var n in c.BlockedNames.Where(n => _shows(n.Category)).OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (files.Any(f => f.FileName.Equals(n.Name, StringComparison.OrdinalIgnoreCase))) continue;   // shown with its file
            Rows.Add(new BlockRow(BlockKind.Name, n.Name, n.Name, Loc.T("Ui.Block.Kind.Name"), Category(n.Category), n.Note));
        }
        foreach (var p in c.BlockedPaths.Where(p => _shows(p.Category)).OrderBy(p => p.Path, StringComparer.OrdinalIgnoreCase))
        {
            Rows.Add(new BlockRow(BlockKind.Folder, p.Path, p.Path, Loc.T("Ui.Block.Kind.Folder"), Category(p.Category), p.Note));
        }
    }

    private static string Category(RuleCategory c) => Loc.T("Ui.Category." + c);
}
