using System;
using System.Linq;
using System.Numerics;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using GatherBuddy.Alarms;
using GatherBuddy.Config;
using GatherBuddy.GatherHelper;
using GatherBuddy.Interfaces;
using GatherBuddy.Plugin;
using GatherBuddy.SeFunctions;
using GatherBuddy.Time;
using ImGuiNET;
using OtterGui;
using OtterGui.Widgets;
using ImRaii = OtterGui.Raii.ImRaii;

namespace GatherBuddy.Gui;

public partial class Interface
{
    private static string CheckUnnamed(string name)
        => name.Length > 0 ? name : "<未命名>";

    private static string CheckUndescribed(string desc)
        => desc.Length > 0 ? desc : "<无描述>";


    private class AlarmWindowDragDropData
    {
        public AlarmGroup Group;
        public Alarm      Alarm;
        public int        AlarmIdx;

        public AlarmWindowDragDropData(AlarmGroup group, Alarm alarm, int idx)
        {
            Group    = group;
            Alarm    = alarm;
            AlarmIdx = idx;
        }
    }

    private class AlarmCache
    {
        public sealed class TimedItemCombo : ClippedSelectableCombo<IGatherable>
        {
            public TimedItemCombo(string label)
                : base("##TimedItem", label, 200, GatherBuddy.UptimeManager.TimedGatherables, i => i.Name[GatherBuddy.Language])
            { }
        }

        public sealed class AlarmSelector : ItemSelector<AlarmGroup>
        {
            private readonly AlarmManager _manager;

            public AlarmSelector(AlarmManager manager)
                : base(manager.Alarms, Flags.All)
                => _manager = manager;

            protected override bool Filtered(int idx)
                => Filter.Length != 0 && !Items[idx].Name.Contains(Filter, StringComparison.InvariantCultureIgnoreCase);

            protected override bool OnDraw(int idx)
            {
                using var id    = ImRaii.PushId(idx);
                using var color = ImRaii.PushColor(ImGuiCol.Text, ColorId.DisabledText.Value(), !Items[idx].Enabled);
                return ImGui.Selectable(CheckUnnamed(Items[idx].Name), idx == CurrentIdx);
            }

            protected override bool OnDelete(int idx)
            {
                _manager.DeleteGroup(idx);
                return true;
            }

            protected override bool OnAdd(string name)
            {
                _manager.AddGroup(name);
                return true;
            }

            protected override bool OnClipboardImport(string name, string data)
            {
                if (!AlarmGroup.Config.FromBase64(data, out var configGroup))
                    return false;

                var group = new AlarmGroup()
                {
                    Name        = name,
                    Description = configGroup.Description,
                    Enabled     = false,
                    Alarms = configGroup.Alarms.Select(a => Alarm.FromConfig(a, out var alarm) ? alarm : null)
                        .Where(a => a != null)
                        .Cast<Alarm>()
                        .ToList(),
                };

                if (group.Alarms.Count < configGroup.Alarms.Count())
                    GatherBuddy.Log.Warning("已跳过无效的闹钟");

                _manager.AddGroup(group);
                return true;
            }

            protected override bool OnDuplicate(string name, int idx)
            {
                var group = _manager.Alarms[idx].Clone();
                group.Name = name;
                _manager.AddGroup(group);
                return true;
            }

            protected override void OnDrop(object? data, int idx)
            {
                if (data is not AlarmWindowDragDropData obj)
                    return;

                var group = _plugin.AlarmManager.Alarms[idx];
                _plugin.AlarmManager.DeleteAlarm(obj.Group, obj.AlarmIdx);
                _plugin.AlarmManager.AddAlarm(group, obj.Alarm);
            }

            protected override bool OnMove(int idx1, int idx2)
            {
                _manager.MoveGroup(idx1, idx2);
                return idx1 != idx2;
            }
        }

        public AlarmCache(AlarmManager manager)
            => Selector = new AlarmSelector(manager);

        public static readonly Sounds[] SoundIds = Enum.GetValues<Sounds>().Where(s => s != Sounds.Unknown).ToArray();

        public static readonly string SoundIdNames =
            string.Join("\0", SoundIds.Select(s => s == Sounds.None ? "无提示音" : $"提示音 {s.ToIdx()}"));

        public readonly AlarmSelector  Selector;
        public readonly TimedItemCombo ItemCombo = new(string.Empty);

        public bool EditGroupName;
        public bool EditGroupDesc;

        public string NewName         = string.Empty;
        public int    NewItemIdx      = 0;
        public bool   NewEnabled      = false;
        public bool   NewPrintMessage = false;
        public int    NewSoundIdx     = 0;
        public int    NewSecondOffset = 0;

        public int ChangedSecondOffset = 0;
        public int ChangedAlarmIdx     = -1;

        public Alarm CreateAlarm()
            => new(GatherBuddy.UptimeManager.TimedGatherables[NewItemIdx])
            {
                Enabled      = NewEnabled,
                SecondOffset = NewSecondOffset,
                PrintMessage = NewPrintMessage,
                Name         = NewName,
                SoundId      = SoundIds[NewSoundIdx],
            };
    }

    private readonly AlarmCache _alarmCache;

    private void DrawAlarmInfo(ref int alarmIdx, AlarmGroup group)
    {
        var       alarm   = group.Alarms[alarmIdx];
        using var id      = ImRaii.PushId(alarmIdx);
        var       enabled = alarm.Enabled;

        ImGui.TableNextColumn();
        if (ImGuiUtil.DrawDisabledButton(FontAwesomeIcon.Trash.ToIconString(), IconButtonSize, "删除此闹钟。", false, true))
            _plugin.AlarmManager.DeleteAlarm(group, alarmIdx--);
        ImGui.TableNextColumn();
        ImGui.SetNextItemWidth(SetInputWidth);
        var name = alarm.Name;
        if (ImGui.InputTextWithHint("##name", CheckUnnamed(string.Empty), ref name, 64))
            _plugin.AlarmManager.ChangeAlarmName(group, alarmIdx, name);
        ImGuiUtil.HoverTooltip("可以命名这个闹钟，这将在聊天栏中的闹钟提示信息中展示。");

        ImGui.TableNextColumn();
        if (ImGui.Checkbox("##Enabled", ref enabled) && enabled != alarm.Enabled)
            _plugin.AlarmManager.ToggleAlarm(group, alarmIdx);
        ImGuiUtil.HoverTooltip("启用这个闹钟。");

        ImGui.TableNextColumn();
        if (_alarmCache.ItemCombo.Draw(alarm.Item.InternalLocationId - 1, out var newIdx))
            _plugin.AlarmManager.ChangeAlarmItem(group, alarmIdx, GatherBuddy.UptimeManager.TimedGatherables[newIdx]);
        _alarmCache.Selector.CreateDropSource(new AlarmWindowDragDropData(group, alarm, alarmIdx), alarm.Item.Name[GatherBuddy.Language]);
        var localIdx = alarmIdx;
        _alarmCache.Selector.CreateDropTarget<AlarmWindowDragDropData>(d => _plugin.AlarmManager.MoveAlarm(group, d.AlarmIdx, localIdx));

        ImGui.TableNextColumn();
        var secondOffset = _alarmCache.ChangedAlarmIdx == alarmIdx ? _alarmCache.ChangedSecondOffset : alarm.SecondOffset;
        ImGui.SetNextItemWidth(SetInputWidth / 2);
        if (ImGui.DragInt("##Offset", ref secondOffset, 0.1f, 0, RealTime.SecondsPerDay))
        {
            _alarmCache.ChangedAlarmIdx     = alarmIdx;
            _alarmCache.ChangedSecondOffset = secondOffset;
        }

        if (ImGui.IsItemDeactivated())
            _plugin.AlarmManager.ChangeAlarmOffset(group, alarmIdx, Math.Clamp(_alarmCache.ChangedSecondOffset, 0, RealTime.SecondsPerDay));
        ImGuiUtil.HoverTooltip("在此物品可采集前的多少秒触发此闹钟。");

        ImGui.TableNextColumn();
        var printMessage = alarm.PrintMessage;
        if (ImGui.Checkbox("##PrintMessage", ref printMessage))
            _plugin.AlarmManager.ChangeAlarmMessage(group, alarmIdx, printMessage);
        ImGuiUtil.HoverTooltip("当此闹钟触发时，在聊天栏打印一条提示信息。");

        ImGui.TableNextColumn();
        var idx = alarm.SoundId.ToIdx();
        ImGui.SetNextItemWidth(85 * ImGuiHelpers.GlobalScale);
        if (ImGui.Combo("##Sound", ref idx, AlarmCache.SoundIdNames))
        {
            _plugin.AlarmManager.ChangeAlarmSound(group, alarmIdx, AlarmCache.SoundIds[idx]);
            _plugin.AlarmManager.PreviewAlarm(AlarmCache.SoundIds[idx]);
        }
        ImGuiUtil.HoverTooltip("当此闹钟触发时，播放此提示音。");

        ImGui.TableNextColumn();
        if (DrawLocationInput(alarm.Item, alarm.PreferLocation, out var newLocation))
            _plugin.AlarmManager.ChangeAlarmLocation(group, alarmIdx, newLocation);

        ImGui.TableNextColumn();
        var (_, time) = AlarmManager.GetUptime(alarm);
        var now  = GatherBuddy.Time.ServerTime.AddSeconds(alarm.SecondOffset);
        var size = Vector2.UnitX * 150 * ImGuiHelpers.GlobalScale;
        if (time.Start > now)
            ImGuiUtil.DrawTextButton(TimeInterval.DurationString(time.Start, now, false), size, ColorId.WarningBg.Value());
        else
            ImGuiUtil.DrawTextButton("当前已触发", size, ColorId.ChangedLocationBg.Value());
    }

    private void DrawGroupData(AlarmGroup group, int idx)
    {
        if (ImGuiUtil.DrawEditButtonText(0, _alarmCache.EditGroupName ? group.Name : CheckUnnamed(group.Name), out var newName,
                ref _alarmCache.EditGroupName, IconButtonSize, SetInputWidth, 64))
            _plugin.AlarmManager.ChangeGroupName(idx, newName);

        if (ImGuiUtil.DrawEditButtonText(1, _alarmCache.EditGroupDesc ? group.Description : CheckUndescribed(group.Description),
                out var newDesc, ref _alarmCache.EditGroupDesc, IconButtonSize, 2 * SetInputWidth, 128))
            _plugin.AlarmManager.ChangeGroupDescription(idx, newDesc);
        var enabled = group.Enabled;
        if (ImGui.Checkbox("启用", ref enabled) && enabled != group.Enabled)
            _plugin.AlarmManager.ToggleGroup(idx);
        ImGuiUtil.HoverTooltip(
            "启用此闹钟组。");
    }

    private void DrawToggleAll(AlarmGroup group)
    {
        ImGui.TableNextColumn();
        ImGui.TableNextColumn();
        ImGui.TableNextColumn();
        var allEnabled = group.Alarms.All(a => a.Enabled);
        var ret        = ImGui.Checkbox("##allEnabled", ref allEnabled);
        ImGuiUtil.HoverTooltip("启用所有闹钟，或禁用所有闹钟。");

        if (!ret)
            return;

        for (var i = 0; i < group.Alarms.Count; ++i)
        {
            if (group.Alarms[i].Enabled != allEnabled)
                _plugin.AlarmManager.ToggleAlarm(@group, i);
        }
    }

    private void DrawAlarmTable(AlarmGroup group, int idx)
    {
        var width = SetInputWidth * 3.35f + ImGui.GetFrameHeight() * 3 + (85 + 150) * ImGuiHelpers.GlobalScale + ItemSpacing.X * 8;
        using var table = ImRaii.Table("##alarms", 9, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.NoKeepColumnsVisible,
            Vector2.UnitX * width);
        if (!table)
            return;

        DrawToggleAll(group);
        ImGui.TableNextRow();
        for (var i = 0; i < group.Alarms.Count; ++i)
            DrawAlarmInfo(ref i, group);

        using var id = ImRaii.PushId(-1);
        ImGui.TableNextColumn();
        if (ImGuiUtil.DrawDisabledButton(FontAwesomeIcon.Plus.ToIconString(), IconButtonSize, "添加新闹钟。", false, true))
            _plugin.AlarmManager.AddAlarm(group, _alarmCache.CreateAlarm());
        ImGui.TableNextColumn();
        ImGui.SetNextItemWidth(SetInputWidth);
        ImGui.InputTextWithHint("##name", CheckUnnamed(string.Empty), ref _alarmCache.NewName, 64);
        ImGui.TableNextColumn();
        ImGui.Checkbox("##enabled", ref _alarmCache.NewEnabled);
        ImGui.TableNextColumn();
        if (_alarmCache.ItemCombo.Draw(_alarmCache.NewItemIdx, out var tmp))
            _alarmCache.NewItemIdx = tmp;
        ImGui.TableNextColumn();
        ImGui.SetNextItemWidth(SetInputWidth / 2);
        if (ImGui.DragInt("##Offset", ref _alarmCache.NewSecondOffset, 0.1f, 0, RealTime.SecondsPerDay))
            _alarmCache.NewSecondOffset = Math.Clamp(_alarmCache.NewSecondOffset, 0, RealTime.SecondsPerDay);
        ImGui.TableNextColumn();
        ImGui.Checkbox("##print", ref _alarmCache.NewPrintMessage);
        ImGui.TableNextColumn();
        ImGui.SetNextItemWidth(85 * ImGuiHelpers.GlobalScale);
        ImGui.Combo("##Sound", ref _alarmCache.NewSoundIdx, AlarmCache.SoundIdNames);
    }

    private void DrawAlarmInfo(AlarmGroup group, int idx)
    {
        using var child = ImRaii.Child("##alarmInfo", -Vector2.One, false, ImGuiWindowFlags.HorizontalScrollbar);
        if (!child)
            return;
        DrawGroupData(group, idx);
        ImGui.NewLine();
        DrawAlarmTable(group, idx);
    }

    private void DrawAlarmGroupHeaderLine()
    {
        if (ImGuiUtil.DrawDisabledButton(FontAwesomeIcon.Copy.ToIconString(), IconButtonSize, "将当前闹钟组复制到剪贴板。",
                _alarmCache.Selector.Current == null, true))
        {
            var group = _alarmCache.Selector.Current!;
            try
            {
                var s = new AlarmGroup.Config(group).ToBase64();
                ImGui.SetClipboardText(s);
                Communicator.PrintClipboardMessage("闹钟组 ", group.Name);
            }
            catch (Exception e)
            {
                GatherBuddy.Log.Error($"无法将闹钟组 {group.Name} 复制到剪贴板：\n{e}。");
                Communicator.PrintClipboardMessage("闹钟组 ", group.Name, e);
            }
        }

        if (ImGuiUtil.DrawDisabledButton("创建预设", Vector2.Zero, "将此闹钟组添加为新的采集窗口预设。",
                _alarmCache.Selector.Current == null))
        {
            var preset = new GatherWindowPreset(_alarmCache.Selector.Current!);
            _plugin.GatherWindowManager.AddPreset(preset);
        }

        ImGui.SameLine();

        ImGuiComponents.HelpMarker("使用 /gather alarm 指令以采集上一个被触发闹钟的矿物/植物。\n"
          + "使用 /gatherfish alarm 指令以采集上一个被触发闹钟的鱼类。");
    }

    private void DrawAlarmTab()
    {
        using var id  = ImRaii.PushId("闹钟");
        using var tab = ImRaii.TabItem("闹钟");
        ImGuiUtil.HoverTooltip("你是否经常在约会中迟到？\n"
          + "在这里设置你的专属闹钟！");
        if (!tab)
            return;

        _alarmCache.Selector.Draw(SelectorWidth);
        ImGui.SameLine();

        ItemDetailsWindow.Draw("Alarm Group Details", DrawAlarmGroupHeaderLine, () =>
        {
            if (_alarmCache.Selector.Current != null)
                DrawAlarmInfo(_alarmCache.Selector.Current, _alarmCache.Selector.CurrentIdx);
        });
    }
}
