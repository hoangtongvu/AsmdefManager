using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace AsmdefManager;

public class AsmdefManagerWindow : EditorWindow
{
    const string DragKey = "AsmdefManager.Entry";
    static readonly Color SelectedColor = new(0.17f, 0.36f, 0.53f, 1f);
    static readonly Color HoverColor = new(1f, 1f, 1f, 0.08f);
    static readonly Color DropColor = new(0.95f, 0.75f, 0.2f, 1f);

    // ----------------------------------------------------------------- state

    [SerializeField] string selectedPath;
    [SerializeField] bool autoApply;
    [SerializeField] bool sortOnApply = true;
    [SerializeField] List<PendingRef> pending = new();
    string search = "";

    readonly List<Entry> entries = new();
    readonly Dictionary<string, AsmInfo> infoByGuid = new();
    readonly Dictionary<string, AsmInfo> infoByName = new();
    ScrollView listScroll;
    VisualElement inspectorHost;
    VisualElement pendingHost;
    VisualElement refsHost;
    Button applyButton;
    Button discardButton;
    // The assembly selected in the list: target of ">" and subject of the 2nd view.
    Entry InspectedEntry => FindByPath(selectedPath);

    [MenuItem("Tools/Asmdef Manager")]
    static void Open()
    {
        var w = GetWindow<AsmdefManagerWindow>();
        w.titleContent = new GUIContent("Asmdef Manager");
        w.minSize = new Vector2(480, 240);
        w.Show();
    }

    // -------------------------------------------------------------------- UI

    void CreateGUI()
    {
        var root = rootVisualElement;
        root.Clear();

        saveChangesMessage = "There are asmdef reference changes that haven't been applied yet. Apply them now?";

        // Search bar (top)
        var toolbar = new Toolbar();
        var searchField = new ToolbarSearchField { value = search };
        searchField.style.flexGrow = 1;
        searchField.style.marginLeft = 4;
        searchField.style.marginRight = 4;
        searchField.RegisterValueChangedCallback(e =>
        {
            search = e.newValue ?? "";
            RebuildList();
        });
        toolbar.Add(searchField);

        var autoToggle = new ToolbarToggle
        {
            text = "Auto apply",
            value = autoApply,
            tooltip = "ON: every added reference is written (and compiled) immediately.\n" +
                      "OFF: references are staged and written together when you press Apply."
        };
        autoToggle.RegisterValueChangedCallback(e => autoApply = e.newValue);
        toolbar.Add(autoToggle);

        var sortToggle = new ToolbarToggle
        {
            text = "Sort on apply",
            value = sortOnApply,
            tooltip = "Also sort the references of every assembly written by Apply:\n" +
                      "Unity registry packages, then user assemblies, then other packages."
        };
        sortToggle.RegisterValueChangedCallback(e => sortOnApply = e.newValue);
        toolbar.Add(sortToggle);
        toolbar.Add(new ToolbarButton(() => ReloadEntries(true)) { text = "Refresh" });
        root.Add(toolbar);

        applyButton = new Button(ApplyPending)
        {
            tooltip = "Write all staged references to disk in one batch (one compile)."
        };
        discardButton = new Button(DiscardPending) { text = "Discard" };

        // Assemblies view: split view
        var split = new TwoPaneSplitView(0, 260, TwoPaneSplitViewOrientation.Horizontal);
        split.style.flexGrow = 1;
        root.Add(split);

        // First view: scroll view with entries
        var left = new VisualElement();
        left.style.flexGrow = 1;
        listScroll = new ScrollView(ScrollViewMode.Vertical);
        listScroll.style.flexGrow = 1;
        left.Add(listScroll);
        split.Add(left);

        // Second view: staged references of the selected assembly
        inspectorHost = new VisualElement();
        inspectorHost.style.flexGrow = 1;
        split.Add(inspectorHost);

        ReloadEntries(true);
    }

    // ---------------------------------------------------------- asset picker

    static readonly int PickerControlId = "AsmdefManager.Picker".GetHashCode();

    // Opens Unity's "Select Assembly Definition Asset" object picker.
    void ShowAssemblyPicker()
    {
        if (InspectedEntry == null) return;
        EditorGUIUtility.ShowObjectPicker<UnityEditorInternal.AssemblyDefinitionAsset>(
            null, false, "", PickerControlId);
    }

    // The object picker reports its result through IMGUI command events.
    void OnGUI()
    {
        var e = Event.current;
        if (e.type != EventType.ExecuteCommand) return;
        if (e.commandName != "ObjectSelectorClosed") return;
        if (EditorGUIUtility.GetObjectPickerControlID() != PickerControlId) return;

        var picked = EditorGUIUtility.GetObjectPickerObject();
        e.Use();
        if (picked == null) return;

        var target = InspectedEntry;
        if (target == null) return;

        var path = AssetDatabase.GetAssetPath(picked);
        var guid = AssetDatabase.AssetPathToGUID(path);
        if (string.IsNullOrEmpty(guid)) return;

        var name = infoByGuid.TryGetValue(guid, out var info) ? info.Name : picked.name;
        StageReference(target, guid, name, FindByGuid(guid));
    }

    // Called by Unity whenever the project's assets change.
    void OnProjectChange()
    {
        if (listScroll == null) return;
        rootVisualElement.schedule.Execute(() => ReloadEntries(false)).ExecuteLater(50);
    }

    // Unity's built-in "unsaved changes" hooks (prompts when the window is closed).
    public override void SaveChanges()
    {
        ApplyPending();
        base.SaveChanges();
    }

    public override void DiscardChanges()
    {
        pending.Clear();
        base.DiscardChanges();
    }

    // ------------------------------------------------------------ list logic

    void ReloadEntries(bool forceInspector)
    {
        entries.Clear();
        infoByGuid.Clear();
        infoByName.Clear();

        foreach (var guid in AssetDatabase.FindAssets("t:AssemblyDefinitionAsset"))
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            if (!path.EndsWith(".asmdef", StringComparison.OrdinalIgnoreCase)) continue;

            try
            {
                var full = GetFullPath(path);
                var data = JsonUtility.FromJson<AsmdefData>(File.ReadAllText(full));
                if (data == null || string.IsNullOrEmpty(data.name)) continue;
                var category = GetCategory(path);
                var info = new AsmInfo { Name = data.name, Guid = guid, Category = category };
                infoByGuid[guid] = info;
                infoByName[data.name] = info;
                if (category == RefCategory.UserAssembly)
                    entries.Add(new Entry { Name = data.name, Path = path, FullPath = full, Guid = guid, Data = data });
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Asmdef Manager] Could not read {path}: {ex.Message}");
            }
        }

        entries.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

        bool inspectorInvalid = forceInspector;

        if (FindByPath(selectedPath) == null)
        {
            selectedPath = null;
            inspectorInvalid = true;
        }

        // Drop staged references whose assemblies no longer exist.
        pending.RemoveAll(p => FindByPath(p.targetPath) == null || FindByGuid(p.sourceGuid) == null);

        RebuildList();
        if (inspectorInvalid) BuildInspector();
        else UpdatePendingPanel();
        UpdateToolbar();
    }

    void RebuildList()
    {
        if (listScroll == null) return;
        listScroll.Clear();

        foreach (var entry in entries)
        {
            if (!string.IsNullOrEmpty(search) &&
                entry.Name.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0)
                continue;
            listScroll.Add(MakeEntryElement(entry));
        }
    }

    VisualElement MakeEntryElement(Entry entry)
    {
        var row = new VisualElement { userData = entry, tooltip = entry.Path };
        row.style.flexDirection = FlexDirection.Row;
        row.style.alignItems = Align.Center;
        row.style.paddingLeft = 6;
        row.style.paddingRight = 0;
        row.style.paddingTop = 2;
        row.style.paddingBottom = 2;
        SetBorder(row, 0, DropColor);

        var label = new Label(entry.Name) { pickingMode = PickingMode.Ignore };
        label.style.flexGrow = 1;
        label.style.flexShrink = 1;
        label.style.overflow = Overflow.Hidden;
        label.style.unityTextAlign = TextAnchor.MiddleLeft;
        row.Add(label);

        // Small status text: "+2" = number of staged references.
        var info = new Label { name = "info", pickingMode = PickingMode.Ignore };
        info.style.fontSize = 10;
        info.style.opacity = 0.8f;
        info.style.marginLeft = 4;
        info.style.marginRight = 4;
        info.style.flexShrink = 0;
        row.Add(info);

        // ">" : add this assembly as a reference of the inspected selected assembly.
        var addBtn = new Button(() =>
        {
            var target = InspectedEntry;
            if (target != null) AddReference(target, entry);
        })
        { name = "addRef", text = ">" };
        addBtn.style.width = 20;
        addBtn.style.flexShrink = 0;

        addBtn.style.borderTopWidth = 0;
        addBtn.style.borderBottomWidth = 0;
        addBtn.style.borderLeftWidth = 0;
        addBtn.style.borderRightWidth = 0;

        addBtn.style.paddingLeft = 0;
        addBtn.style.paddingRight = 0;
        addBtn.style.paddingTop = 0;
        addBtn.style.paddingBottom = 0;

        // Don't let a press on the button start a select/drag on the row.
        addBtn.RegisterCallback<PointerDownEvent>(e => e.StopPropagation());
        row.Add(addBtn);

        // ---- click-to-select on mouse up, drag-to-reference ----
        bool pressed = false;
        Vector3 pressPos = default;

        row.RegisterCallback<PointerEnterEvent>(_ => SetRowBackground(row, true));
        row.RegisterCallback<PointerLeaveEvent>(_ => SetRowBackground(row, false));

        row.RegisterCallback<PointerDownEvent>(e =>
        {
            if (e.button != 0) return;
            pressed = true;
            pressPos = e.position;
            row.CapturePointer(e.pointerId);
        });

        row.RegisterCallback<PointerMoveEvent>(e =>
        {
            if (!pressed) return;
            if ((e.position - pressPos).sqrMagnitude < 25f) return; // 5px threshold

            pressed = false;
            row.ReleasePointer(e.pointerId);

            DragAndDrop.PrepareStartDrag();
            DragAndDrop.SetGenericData(DragKey, entry);
            DragAndDrop.StartDrag(entry.Name);
        });

        row.RegisterCallback<PointerUpEvent>(e =>
        {
            if (!pressed || e.button != 0) return;
            pressed = false;
            row.ReleasePointer(e.pointerId);
            if (row.worldBound.Contains(e.position))
                Select(entry);
        });

        row.RegisterCallback<PointerCaptureOutEvent>(_ => pressed = false);

        // ---- drop target: dragging A onto B adds A to B's references ----
        row.RegisterCallback<DragEnterEvent>(_ =>
        {
            if (CanDrop(entry)) SetBorder(row, 1, DropColor);
        });
        row.RegisterCallback<DragLeaveEvent>(_ => SetBorder(row, 0, DropColor));
        row.RegisterCallback<DragExitedEvent>(_ => SetBorder(row, 0, DropColor));
        row.RegisterCallback<DragUpdatedEvent>(_ =>
        {
            DragAndDrop.visualMode = CanDrop(entry)
                ? DragAndDropVisualMode.Link
                : DragAndDropVisualMode.Rejected;
        });
        row.RegisterCallback<DragPerformEvent>(_ =>
        {
            SetBorder(row, 0, DropColor);
            if (!CanDrop(entry)) return;
            DragAndDrop.AcceptDrag();
            var source = (Entry)DragAndDrop.GetGenericData(DragKey);
            AddReference(entry, source);
        });

        RefreshRow(row);
        return row;
    }

    static bool CanDrop(Entry target)
    {
        var source = DragAndDrop.GetGenericData(DragKey) as Entry;
        return source != null && source != target;
    }

    static void SetBorder(VisualElement ve, float width, Color color)
    {
        ve.style.borderTopWidth = ve.style.borderBottomWidth =
            ve.style.borderLeftWidth = ve.style.borderRightWidth = width;
        ve.style.borderTopColor = ve.style.borderBottomColor =
            ve.style.borderLeftColor = ve.style.borderRightColor = color;
    }

    void SetRowBackground(VisualElement row, bool hover)
    {
        var entry = (Entry)row.userData;
        if (entry.Path == selectedPath)
            row.style.backgroundColor = SelectedColor;
        else
            row.style.backgroundColor = hover ? HoverColor : Color.clear;
    }

    // Refreshes background, status text and the ">" button of one row.
    void RefreshRow(VisualElement row)
    {
        var entry = (Entry)row.userData;
        var target = InspectedEntry;

        SetRowBackground(row, false);

        int stagedCount = pending.Count(p => p.targetPath == entry.Path);
        var infoParts = new List<string>();
        if (stagedCount > 0) infoParts.Add("+" + stagedCount);
        row.Q<Label>("info").text = string.Join(" ", infoParts);

        var btn = row.Q<Button>("addRef");
        if (target == null)
        {
            btn.SetEnabled(false);
            btn.tooltip = "Select an assembly first";
        }
        else if (target == entry)
        {
            btn.SetEnabled(false);
            btn.tooltip = "Can't reference itself";
        }
        else if (EffectiveContains(target, entry))
        {
            btn.SetEnabled(false);
            btn.tooltip = $"'{target.Name}' already references '{entry.Name}'";
        }
        else
        {
            btn.SetEnabled(true);
            btn.tooltip = $"Add '{entry.Name}' as a reference of '{target.Name}'";
        }
    }

    void RefreshRows()
    {
        if (listScroll == null) return;
        foreach (var child in listScroll.contentContainer.Children())
            RefreshRow(child);
    }

    // Refreshes everything that depends on selection / pin / staged references.
    void RefreshAll()
    {
        RefreshRows();
        UpdatePendingPanel();
        UpdateToolbar();
    }

    void UpdateToolbar()
    {
        if (applyButton == null) return;
        applyButton.text = $"Apply ({pending.Count})";
        applyButton.SetEnabled(pending.Count > 0);
        discardButton.SetEnabled(pending.Count > 0);
        hasUnsavedChanges = pending.Count > 0;
    }

    // -------------------------------------------------------------- selection

    void Select(Entry entry)
    {
        selectedPath = entry.Path;

        // Let Unity's own Inspector show the asmdef (Revert/Apply included).
        var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(entry.Path);
        if (asset != null) Selection.activeObject = asset;

        RefreshRows();
        BuildInspector();
    }

    // 2nd view: staged + categorized references of the selected assembly.
    void BuildInspector()
    {
        if (inspectorHost == null) return;

        inspectorHost.Clear();
        pendingHost = null;
        refsHost = null;

        // Show nothing if no entry is selected.
        var entry = InspectedEntry;
        if (entry == null) return;

        var header = new VisualElement();
        header.style.flexDirection = FlexDirection.Row;
        header.style.alignItems = Align.Center;
        header.style.paddingLeft = 6;
        header.style.paddingRight = 6;
        header.style.paddingTop = 4;
        header.style.paddingBottom = 4;

        var title = new Label(entry.Name);
        title.style.unityFontStyleAndWeight = FontStyle.Bold;
        title.style.flexGrow = 1;
        title.style.overflow = Overflow.Hidden;
        header.Add(title);
        header.Add(new Button(() => SortReferences(entry))
        {
            text = "Sort references",
            tooltip = "Rewrite this asmdef's references ordered by category, then by name.\n" +
                      "Staged references for it are written too."
        });
        inspectorHost.Add(header);

        pendingHost = new VisualElement();
        inspectorHost.Add(pendingHost);

        var scroll = new ScrollView(ScrollViewMode.Vertical);
        scroll.style.flexGrow = 1;
        refsHost = new VisualElement();
        scroll.Add(refsHost);
        inspectorHost.Add(scroll);

        UpdatePendingPanel();
        UpdateToolbar();
    }

    // Shows the assembly's references (disk + staged) grouped by category.
    void UpdateRefsPanel()
    {
        if (refsHost == null) return;
        refsHost.Clear();

        var entry = InspectedEntry;
        if (entry == null) return;

        var disk = entry.Data.references ?? new string[0];
        bool fileSorted = disk.SequenceEqual(SortRefs(disk));
        var staged = new HashSet<string>(
            pending.Where(p => p.targetPath == entry.Path).Select(p => "GUID:" + p.sourceGuid));
        var sortedAll = SortRefs(EffectiveRefs(entry));

        refsHost.style.paddingLeft = 6;
        refsHost.style.paddingRight = 6;

        var headingRow = new VisualElement();
        headingRow.style.flexDirection = FlexDirection.Row;
        headingRow.style.alignItems = Align.Center;
        headingRow.style.marginBottom = 2;

        var heading = new Label(fileSorted ? "References" : "References <color=yellow>(unsorted)</color>");
        heading.style.unityFontStyleAndWeight = FontStyle.Bold;
        headingRow.Add(heading);

        var plusBtn = new Button(ShowAssemblyPicker)
        {
            text = "+",
            tooltip = $"Pick an Assembly Definition Asset to add as a reference of '{entry.Name}'"
        };
        plusBtn.style.width = 20;
        plusBtn.style.marginLeft = 4;
        headingRow.Add(plusBtn);
        refsHost.Add(headingRow);

        if (sortedAll.Count == 0)
        {
            var none = new Label("No references.");
            none.style.opacity = 0.6f;
            refsHost.Add(none);
            return;
        }

        foreach (RefCategory cat in Enum.GetValues(typeof(RefCategory)))
        {
            var items = sortedAll.Where(r => CategoryOf(r) == cat).ToList();
            if (items.Count == 0) continue;

            var catLabel = new Label($"{CategoryLabel(cat)} ({items.Count})");
            catLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            catLabel.style.fontSize = 11;
            catLabel.style.marginTop = 4;
            refsHost.Add(catLabel);

            foreach (var r in items)
            {
                bool isStaged = staged.Contains(r);

                var line = new VisualElement();
                line.style.height = 18;
                line.style.flexDirection = FlexDirection.Row;
                line.style.alignItems = Align.Center;

                var name = new Label(RefName(r));
                name.style.flexGrow = 1;
                name.style.opacity = 0.8f;
                name.style.paddingLeft = 10;
                if (isStaged)
                {
                    name.style.color = Color.green;
                }
                line.Add(name);

                var discardChangeBtn = new Button(() =>
                {
                    var captured = pending.FirstOrDefault(p => p.targetPath == entry.Path && p.sourceGuid == r.Split(':')[1]);
                    pending.Remove(captured);
                    RefreshAll();
                })
                { text = "x", tooltip = "Discard this staged reference" };

                line.RegisterCallback<PointerEnterEvent>(_ =>
                {
                    line.style.backgroundColor = HoverColor;
                    if (isStaged) line.Add(discardChangeBtn);
                });
                line.RegisterCallback<PointerLeaveEvent>(_ =>
                {
                    line.style.backgroundColor = Color.clear;
                    if (isStaged) line.Remove(discardChangeBtn);
                });

                refsHost.Add(line);
            }
        }
    }

    // Lists the staged (not yet written) references of the inspected assembly.
    void UpdatePendingPanel()
    {
        UpdateRefsPanel();
        if (pendingHost == null) return;
        pendingHost.Clear();

        var entry = InspectedEntry;
        var items = entry == null
            ? new List<PendingRef>()
            : pending.Where(p => p.targetPath == entry.Path).ToList();

        if (items.Count == 0)
        {
            var hint = new Label("No staged references.");
            hint.style.paddingLeft = 6;
            hint.style.opacity = 0.6f;
            hint.style.whiteSpace = WhiteSpace.Normal;
            pendingHost.style.backgroundColor = Color.clear;
            pendingHost.Add(hint);
            return;
        }

        pendingHost.style.backgroundColor = new Color(0.95f, 0.75f, 0.2f, 0.12f);
        pendingHost.style.paddingLeft = 6;
        pendingHost.style.paddingRight = 6;
        pendingHost.style.paddingTop = 4;
        pendingHost.style.paddingBottom = 4;
        pendingHost.style.marginBottom = 4;

        var headingRow = new VisualElement();
        headingRow.style.flexDirection = FlexDirection.Row;
        headingRow.style.alignItems = Align.Center;

        var headingLabel = new Label("Staged references (not applied yet)");
        headingLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
        headingLabel.style.flexGrow = 1;
        headingRow.Add(headingLabel);

        headingRow.Add(applyButton);
        headingRow.Add(discardButton);

        pendingHost.Add(headingRow);
    }

    // ------------------------------------------------------------ references

    // Stages "source" as a reference of "target". Nothing is written (and nothing
    // compiles) until ApplyPending runs, unless "Auto apply" is on.
    void AddReference(Entry target, Entry source)
    {
        if (target == source) return;
        StageReference(target, source.Guid, source.Name, source);
    }

    // sourceEntry is null when the source is not a user assembly (e.g. a package asmdef).
    void StageReference(Entry target, string sourceGuid, string sourceName, Entry sourceEntry)
    {
        if (target.Guid == sourceGuid)
        {
            ShowNotification(new GUIContent("An assembly can't reference itself"));
            return;
        }

        if (EffectiveRefs(target).Any(r => ResolveInfo(r)?.Guid == sourceGuid))
        {
            ShowNotification(new GUIContent($"{target.Name} already references {sourceName}"));
            return;
        }

        // Unity rejects cyclic assembly references, so check (including staged ones).
        if (sourceEntry != null && Reaches(sourceEntry, target, new HashSet<Entry>()))
        {
            ShowNotification(new GUIContent(
                $"Cyclic reference: {sourceName} already depends on {target.Name}"));
            return;
        }

        pending.Add(new PendingRef { targetPath = target.Path, sourceGuid = sourceGuid });

        if (autoApply)
        {
            ApplyPending();
        }
        else
        {
            RefreshAll();
            ShowNotification(new GUIContent($"Staged '{sourceName}' > '{target.Name}'"));
        }
    }

    void ApplyPending() => WriteReferences(null, sortOnApply);

    void SortReferences(Entry target) => WriteReferences(target.Path, true);

    // Writes staged references (and optionally sorts the references) inside ONE
    // asset-editing batch, so Unity imports the changed asmdefs together and compiles once.
    // onlyPath == null: every assembly with staged references; otherwise just that asmdef.
    void WriteReferences(string onlyPath, bool sort)
    {
        var paths = onlyPath != null
            ? new List<string> { onlyPath }
            : pending.Select(p => p.targetPath).Distinct().ToList();
        if (paths.Count == 0) return;

        int written = 0;
        AssetDatabase.StartAssetEditing();
        try
        {
            foreach (var path in paths)
            {
                var target = FindByPath(path);
                if (target == null) continue;

                // Re-read from disk so we never overwrite edits made elsewhere.
                var data = JsonUtility.FromJson<AsmdefData>(File.ReadAllText(target.FullPath));
                var original = data.references ?? new string[0];
                var refs = original.ToList();
                foreach (var p in pending.Where(p => p.targetPath == path))
                {
                    var r = "GUID:" + p.sourceGuid;
                    if (!refs.Contains(r)) refs.Add(r);
                }
                if (sort) refs = SortRefs(refs);

                if (refs.SequenceEqual(original)) continue;

                data.references = refs.ToArray();
                File.WriteAllText(target.FullPath, JsonUtility.ToJson(data, true));
                AssetDatabase.ImportAsset(target.Path, ImportAssetOptions.ForceUpdate);
                written++;
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"[Asmdef Manager] Failed to write references: {ex}");
            return;
        }
        finally
        {
            AssetDatabase.StopAssetEditing();
        }

        pending.RemoveAll(p => paths.Contains(p.targetPath));
        ShowNotification(new GUIContent(written == 0
            ? "Nothing to change"
            : $"Updated references of {written} assembl{(written == 1 ? "y" : "ies")}"));
        ReloadEntries(true);
    }

    void DiscardPending()
    {
        pending.Clear();
        RefreshAll();
    }

    // Disk references + staged references.
    IEnumerable<string> EffectiveRefs(Entry e)
    {
        var onDisk = e.Data.references ?? new string[0];
        var staged = pending.Where(p => p.targetPath == e.Path).Select(p => "GUID:" + p.sourceGuid);
        return onDisk.Concat(staged);
    }

    bool EffectiveContains(Entry owner, Entry candidate)
    {
        foreach (var r in EffectiveRefs(owner))
            if (Resolve(r) == candidate) return true;
        return false;
    }

    // True if 'from' depends (directly or transitively) on 'to'.
    bool Reaches(Entry from, Entry to, HashSet<Entry> visited)
    {
        if (!visited.Add(from)) return false;
        foreach (var r in EffectiveRefs(from))
        {
            var next = Resolve(r);
            if (next == null) continue;
            if (next == to) return true;
            if (Reaches(next, to, visited)) return true;
        }
        return false;
    }

    // ---- reference categorization / sorting ----

    // References are stored either as "GUID:xxxx" or by assembly name.
    AsmInfo ResolveInfo(string reference)
    {
        if (string.IsNullOrEmpty(reference)) return null;
        AsmInfo info;
        if (reference.StartsWith("GUID:", StringComparison.OrdinalIgnoreCase))
            return infoByGuid.TryGetValue(reference.Substring(5), out info) ? info : null;
        return infoByName.TryGetValue(reference, out info) ? info : null;
    }

    RefCategory CategoryOf(string reference)
    {
        var info = ResolveInfo(reference);
        return info != null ? info.Category : RefCategory.Unknown;
    }

    string RefName(string reference)
    {
        var info = ResolveInfo(reference);
        return info != null ? info.Name : reference;
    }

    static string CategoryLabel(RefCategory c)
    {
        switch (c)
        {
            case RefCategory.Registry: return "Registry packages";
            case RefCategory.BuiltIn: return "Built-in packages";
            case RefCategory.Embedded: return "Embedded packages";
            case RefCategory.Local: return "Local packages";
            case RefCategory.Git: return "Git packages";
            case RefCategory.LocalTarball: return "Local Tarball packages";
            case RefCategory.UserAssembly: return "User-defined assemblies";
            default: return "Unknown packages";
        }
    }

    // Order: Unity registry packages, user assemblies, other packages; then by name.
    List<string> SortRefs(IEnumerable<string> refs)
    {
        return refs.Distinct()
            .OrderBy(r => (int)CategoryOf(r))
            .ThenBy(r => RefName(r), StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // References are stored either as "GUID:xxxx" or by assembly name.
    Entry Resolve(string reference)
    {
        if (string.IsNullOrEmpty(reference)) return null;
        if (reference.StartsWith("GUID:", StringComparison.OrdinalIgnoreCase))
            return FindByGuid(reference.Substring(5));
        return entries.FirstOrDefault(e => e.Name == reference);
    }

    Entry FindByPath(string path) =>
        string.IsNullOrEmpty(path) ? null : entries.FirstOrDefault(e => e.Path == path);

    Entry FindByGuid(string guid) =>
        string.IsNullOrEmpty(guid) ? null : entries.FirstOrDefault(e => e.Guid == guid);

    // --------------------------------------------------------------- helpers

    // Unity registry: built-in modules and packages from Unity's default registry.
    // User-defined: anything under Assets/ plus embedded/local packages.
    // Other: git, tarball and third-party-registry packages.
    static RefCategory GetCategory(string assetPath)
    {
        var pkg = PackageInfo.FindForAssetPath(assetPath);
        if (pkg == null)
            return assetPath.StartsWith("Assets/", StringComparison.Ordinal)
                ? RefCategory.UserAssembly
                : RefCategory.Unknown;

        switch (pkg.source)
        {
            case PackageSource.Registry:
                return RefCategory.Registry;
            case PackageSource.BuiltIn:
                return RefCategory.BuiltIn;
            case PackageSource.Embedded:
                return RefCategory.Embedded;
            case PackageSource.Local:
                return RefCategory.Local;
            case PackageSource.Git:
                return RefCategory.Git;
            case PackageSource.LocalTarball:
                return RefCategory.LocalTarball;
            default:
                return RefCategory.Unknown;
        }
    }

    static string GetFullPath(string assetPath)
    {
        var pkg = PackageInfo.FindForAssetPath(assetPath);
        if (pkg != null && assetPath.Length > pkg.assetPath.Length)
        {
            var relative = assetPath.Substring(pkg.assetPath.Length).TrimStart('/');
            return System.IO.Path.GetFullPath(System.IO.Path.Combine(pkg.resolvedPath, relative));
        }
        return System.IO.Path.GetFullPath(assetPath);
    }
}