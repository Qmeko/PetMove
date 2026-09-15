using System;
using System.Globalization;
using System.Numerics;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace PetMove;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/petmove";
    private const int PlaceActionId = 3; // 移動
    private const int HeelActionId = 2;  // 追従
    private const int PlaceCommandId = 1800;
    private const int MinCommandIntervalMs = 100;

    [PluginService] private static ICommandManager CommandManager { get; set; } = null!;
    [PluginService] private static IChatGui Chat { get; set; } = null!;
    [PluginService] private static IBuddyList Buddies { get; set; } = null!;
    [PluginService] private static IObjectTable Objects { get; set; } = null!;
    [PluginService] private static ITargetManager Targets { get; set; } = null!;
    [PluginService] private static IPluginLog Log { get; set; } = null!;

    private DateTime nextAllowedUtc = DateTime.MinValue;

    public Plugin()
    {
        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "ペットを座標へ移動: /petmove <x> <y> <z>  /petmove cursor|here|target|follow|list",
        });
    }

    public void Dispose()
    {
        CommandManager.RemoveHandler(CommandName);
    }

    private void OnCommand(string command, string args)
    {
        var parts = (args ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            PrintHelp();
            return;
        }

        var verb = parts[0];
        if (IsToken(verb, "follow", "追従"))
        {
            HeelPet();
            return;
        }

        if (IsToken(verb, "list", "一覧"))
        {
            PrintStatus();
            return;
        }

        if (IsToken(verb, "cursor", "カーソル"))
        {
            PrintCursor();
            return;
        }

        if (!TryResolveDestination(parts, out var dest, out var requiredName, out var error))
        {
            Chat.PrintError(error);
            return;
        }

        PlacePet(dest, requiredName);
    }

    private static void PrintHelp()
    {
        Chat.Print("PetMove の使い方:");
        Chat.Print("  /petmove 100 0 100          指定座標へ移動（Y は高さ）");
        Chat.Print("  /petmove エオス 100 0 100   名前が一致するときだけ移動");
        Chat.Print("  /petmove here               自分の足元へ");
        Chat.Print("  /petmove target             ターゲットの足元へ");
        Chat.Print("  /petmove follow             追従に戻す");
        Chat.Print("  /petmove list               今のペット名と座標を表示");
        Chat.Print("  /petmove cursor             今指している地面の座標を1回表示");
    }

    private static void PrintStatus()
    {
        var player = Objects.LocalPlayer;
        if (player != null)
            Chat.Print($"自分: ({Fmt(player.Position)})");

        var target = Targets.Target;
        if (target != null)
            Chat.Print($"ターゲット「{target.Name}」: ({Fmt(target.Position)})");

        var pet = Buddies.PetBuddy?.GameObject;
        if (pet == null)
            Chat.Print("ペット: 出ていません");
        else
            Chat.Print($"ペット「{pet.Name}」: ({Fmt(pet.Position)})");
    }

    private static void PrintCursor()
    {
        if (!TryGetCursorGround(out var dest))
        {
            Chat.PrintError("カーソルの下に地面がありません。地面を指してから再実行してください。");
            return;
        }

        Chat.Print($"カーソル: {CmdArgs(dest)}");
    }

    private void HeelPet()
    {
        if (!TryGetOwnPet(out _, out var error))
        {
            Chat.PrintError(error);
            return;
        }

        if (!TryUseHeel())
        {
            Chat.PrintError("追従命令を送れませんでした。");
            return;
        }

        Chat.Print("ペットを追従に戻しました。");
    }

    private void PlacePet(Vector3 dest, string? requiredName)
    {
        if (IsInPvpArea())
        {
            Chat.PrintError("PvP エリアでは使えません。");
            return;
        }

        if (!TryGetOwnPet(out var petName, out var error))
        {
            Chat.PrintError(error);
            return;
        }

        if (requiredName != null &&
            !petName.Contains(requiredName, StringComparison.OrdinalIgnoreCase))
        {
            Chat.PrintError($"今のペットは「{petName}」です。「{requiredName}」ではありません。");
            return;
        }

        if (!TryPlaceAt(dest))
        {
            Chat.PrintError("移動命令を送れませんでした。少し待ってから再実行してください。");
            return;
        }

        Chat.Print($"ペット「{petName}」を ({Fmt(dest)}) へ移動させます。");
        Log.Information("Place {Pet} -> {X:0.##} {Y:0.##} {Z:0.##}", petName, dest.X, dest.Y, dest.Z);
    }

    private static bool TryResolveDestination(
        string[] parts,
        out Vector3 dest,
        out string? requiredName,
        out string error)
    {
        dest = default;
        requiredName = null;
        error = string.Empty;

        if (parts.Length == 1 && IsToken(parts[0], "here", "ここ"))
            return TryPlayerPosition(out dest, out error);

        if (parts.Length == 1 && IsToken(parts[0], "target", "ターゲット"))
            return TryTargetPosition(out dest, out error);

        if (parts.Length == 2 && IsToken(parts[1], "here", "ここ"))
        {
            requiredName = parts[0];
            return TryPlayerPosition(out dest, out error);
        }

        if (parts.Length == 2 && IsToken(parts[1], "target", "ターゲット"))
        {
            requiredName = parts[0];
            return TryTargetPosition(out dest, out error);
        }

        if (parts.Length >= 4 && TryParseXyz(parts[1], parts[2], parts[3], out dest))
        {
            requiredName = parts[0];
            return true;
        }

        if (parts.Length >= 3 && TryParseXyz(parts[0], parts[1], parts[2], out dest))
            return true;

        error = "引数が正しくありません。 /petmove  で使い方を見てください。";
        return false;
    }

    private static bool TryGetCursorGround(out Vector3 dest)
    {
        dest = default;
        unsafe
        {
            var am = ActionManager.Instance();
            if (am == null)
                return false;

            var pos = default(Vector3);
            if (!am->GetGroundPositionForCursor(&pos))
                return false;

            dest = pos;
            return true;
        }
    }

    private static bool TryPlayerPosition(out Vector3 dest, out string error)
    {
        dest = default;
        error = string.Empty;
        if (Objects.LocalPlayer is not { } player)
        {
            error = "自分の座標が取れません。";
            return false;
        }

        dest = player.Position;
        return true;
    }

    private static bool TryTargetPosition(out Vector3 dest, out string error)
    {
        dest = default;
        error = string.Empty;
        if (Targets.Target is not { } target)
        {
            error = "ターゲットがいません。";
            return false;
        }

        dest = target.Position;
        return true;
    }

    private static bool TryGetOwnPet(out string petName, out string error)
    {
        petName = string.Empty;
        error = string.Empty;

        var pet = Buddies.PetBuddy?.GameObject;
        if (pet == null)
        {
            error = "ペットが出ていません。先に召喚してください。";
            return false;
        }

        petName = pet.Name.TextValue;
        return true;
    }

    private static bool IsInPvpArea()
    {
        unsafe
        {
            return GameMain.IsInPvPArea();
        }
    }

    private bool TryPlaceAt(Vector3 dest)
    {
        if (!TryConsumeRateLimit())
            return false;

        unsafe
        {
            return GameMain.ExecuteLocationCommand(PlaceCommandId, &dest, PlaceActionId);
        }
    }

    private static bool TryUseHeel()
    {
        unsafe
        {
            var am = ActionManager.Instance();
            if (am == null)
                return false;

            return am->UseAction(ActionType.PetAction, HeelActionId);
        }
    }

    private bool TryConsumeRateLimit()
    {
        var now = DateTime.UtcNow;
        if (now < nextAllowedUtc)
            return false;

        nextAllowedUtc = now.AddMilliseconds(MinCommandIntervalMs);
        return true;
    }

    private static bool TryParseXyz(string sx, string sy, string sz, out Vector3 dest)
    {
        dest = default;
        var culture = CultureInfo.InvariantCulture;
        if (!float.TryParse(sx, culture, out var x))
            return false;
        if (!float.TryParse(sy, culture, out var y))
            return false;
        if (!float.TryParse(sz, culture, out var z))
            return false;

        dest = new Vector3(x, y, z);
        return true;
    }

    private static bool IsToken(string value, params string[] names)
    {
        foreach (var name in names)
        {
            if (value.Equals(name, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static string Fmt(Vector3 v) => $"{v.X:0.##}, {v.Y:0.##}, {v.Z:0.##}";

    private static string CmdArgs(Vector3 v) =>
        string.Create(CultureInfo.InvariantCulture, $"{v.X:0.##} {v.Y:0.##} {v.Z:0.##}");
}
