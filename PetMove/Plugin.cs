using System;
using System.Globalization;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace PetMove;

public sealed class Plugin : IDalamudPlugin
{
    private const string PetCommand = "/petmove";
    private const string MoveCommand = "/cmove";
    private const int PlaceActionId = 3;
    private const int HeelActionId = 2;
    private const int PlaceCommandId = 1800;
    private const int MinCommandIntervalMs = 100;

    [PluginService] private static IDalamudPluginInterface PluginInterface { get; set; } = null!;
    [PluginService] private static ICommandManager CommandManager { get; set; } = null!;
    [PluginService] private static IChatGui Chat { get; set; } = null!;
    [PluginService] private static IBuddyList Buddies { get; set; } = null!;
    [PluginService] private static IObjectTable Objects { get; set; } = null!;
    [PluginService] private static ITargetManager Targets { get; set; } = null!;
    [PluginService] private static IPluginLog Log { get; set; } = null!;

    private readonly ICallGateSubscriber<Vector3, bool, bool> pathfindAndMoveTo;
    private DateTime nextAllowedUtc = DateTime.MinValue;

    public Plugin()
    {
        pathfindAndMoveTo = PluginInterface.GetIpcSubscriber<Vector3, bool, bool>("vnavmesh.SimpleMove.PathfindAndMoveTo");

        CommandManager.AddHandler(PetCommand, new CommandInfo(OnPetCommand)
        {
            HelpMessage = "ペット移動: /petmove <x> <y> <z>  /petmove cursor|here|target|follow|list",
        });
        CommandManager.AddHandler(MoveCommand, new CommandInfo(OnCmove)
        {
            HelpMessage = "座標移動: /cmove pet|player <x> <y> <z>  /cmove cursor|cursorrel  /cmove pet|player offset ...",
        });
    }

    public void Dispose()
    {
        CommandManager.RemoveHandler(PetCommand);
        CommandManager.RemoveHandler(MoveCommand);
    }

    private void OnPetCommand(string command, string args)
    {
        var parts = SplitArgs(args);
        if (parts.Length == 0)
        {
            PrintPetHelp();
            return;
        }

        DispatchMove("pet", parts);
    }

    private void OnCmove(string command, string args)
    {
        var parts = SplitArgs(args);
        if (parts.Length == 0)
        {
            PrintCmoveHelp();
            return;
        }

        if (IsToken(parts[0], "list", "一覧"))
        {
            PrintStatus();
            return;
        }

        if (IsToken(parts[0], "cursor", "カーソル"))
        {
            PrintCursor();
            return;
        }

        if (IsToken(parts[0], "cursorrel", "相対"))
        {
            PrintCursorRel(parts.Length >= 2 ? parts[1] : "target");
            return;
        }

        if (IsToken(parts[0], "stop", "停止"))
        {
            StopPlayer();
            return;
        }

        if (IsToken(parts[0], "pet", "ペット") || IsToken(parts[0], "player", "自分"))
        {
            if (parts.Length == 1)
            {
                Chat.PrintError("続きを書いてください。例: /cmove player 100 0 100");
                return;
            }

            DispatchMove(parts[0], parts[1..]);
            return;
        }

        Chat.PrintError("最初は pet / player / cursor / cursorrel / list / stop です。 /cmove  で使い方を見てください。");
    }

    private void DispatchMove(string who, string[] parts)
    {
        if (parts.Length == 0)
        {
            Chat.PrintError("行き先がありません。");
            return;
        }

        if (IsToken(parts[0], "follow", "追従"))
        {
            if (!IsPet(who))
            {
                Chat.PrintError("follow はペット専用です。");
                return;
            }

            HeelPet();
            return;
        }

        if (IsToken(parts[0], "stop", "停止"))
        {
            if (IsPet(who))
            {
                Chat.PrintError("ペットの停止は /petmove follow を使ってください。");
                return;
            }

            StopPlayer();
            return;
        }

        if (IsToken(parts[0], "list", "一覧"))
        {
            PrintStatus();
            return;
        }

        if (IsToken(parts[0], "cursor", "カーソル"))
        {
            PrintCursor();
            return;
        }

        if (!TryResolveDestination(parts, out var dest, out var requiredName, out var error))
        {
            Chat.PrintError(error);
            return;
        }

        if (IsPet(who))
            PlacePet(dest, requiredName);
        else
            MovePlayer(dest);
    }

    private static void PrintCmoveHelp()
    {
        Chat.Print("CMove の使い方:");
        Chat.Print("  調べる");
        Chat.Print("    /cmove cursor              マップ座標を1回表示");
        Chat.Print("    /cmove cursorrel           ターゲット基準の相対座標");
        Chat.Print("    /cmove cursorrel 123       EntityId 基準の相対座標");
        Chat.Print("    /cmove list                自分・タゲ・ペットの座標とID");
        Chat.Print("  マップ座標へ行く");
        Chat.Print("    /cmove pet 100 0 100");
        Chat.Print("    /cmove player 100 0 100");
        Chat.Print("    /cmove pet cursor|here|target");
        Chat.Print("    /cmove player cursor|here|target");
        Chat.Print("    /cmove player object 123");
        Chat.Print("  相対座標へ行く（原点＋ズレ）");
        Chat.Print("    /cmove pet offset target 5 0 -3");
        Chat.Print("    /cmove player offset 123 5 0 -3");
        Chat.Print("  /cmove player stop           自分の移動を止める");
        Chat.Print("  /petmove も今までどおり使えます。プレイヤー移動には vnavmesh が必要です。");
    }

    private static void PrintPetHelp()
    {
        Chat.Print("PetMove の使い方:");
        Chat.Print("  /petmove 100 0 100          指定座標へ移動（Y は高さ）");
        Chat.Print("  /petmove エオス 100 0 100   名前が一致するときだけ移動");
        Chat.Print("  /petmove here|target|cursor");
        Chat.Print("  /petmove follow             追従に戻す");
        Chat.Print("  /petmove list               座標とIDを表示");
        Chat.Print("  自分の移動は /cmove player ... です。");
    }

    private static void PrintStatus()
    {
        var player = Objects.LocalPlayer;
        if (player != null)
            Chat.Print($"自分 EntityId {player.EntityId}: ({Fmt(player.Position)})");

        var target = Targets.Target;
        if (target != null)
            Chat.Print($"ターゲット「{target.Name}」 EntityId {target.EntityId}: ({Fmt(target.Position)})");

        var pet = Buddies.PetBuddy?.GameObject;
        if (pet == null)
            Chat.Print("ペット: 出ていません");
        else
            Chat.Print($"ペット「{pet.Name}」 EntityId {pet.EntityId}: ({Fmt(pet.Position)})");
    }

    private static void PrintCursor()
    {
        if (!TryGetCursorGround(out var dest))
        {
            Chat.PrintError("カーソルの下に地面がありません。地面を指してから再実行してください。");
            return;
        }

        Chat.Print($"マップ座標: {CmdArgs(dest)}");
        Chat.Print("実行例: /cmove player " + CmdArgs(dest));
    }

    private static void PrintCursorRel(string originToken)
    {
        if (!TryGetCursorGround(out var cursor))
        {
            Chat.PrintError("カーソルの下に地面がありません。地面を指してから再実行してください。");
            return;
        }

        if (!TryFindObject(originToken, out var origin, out var error))
        {
            Chat.PrintError(error);
            return;
        }

        var rel = cursor - origin.Position;
        Chat.Print($"原点「{origin.Name}」 EntityId {origin.EntityId}: ({Fmt(origin.Position)})");
        Chat.Print($"マップ座標: {CmdArgs(cursor)}");
        Chat.Print($"相対座標: {CmdArgs(rel)}");
        Chat.Print($"実行例: /cmove player offset {origin.EntityId} {CmdArgs(rel)}");
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

    private void MovePlayer(Vector3 dest)
    {
        if (IsInPvpArea())
        {
            Chat.PrintError("PvP エリアでは使えません。");
            return;
        }

        try
        {
            if (!pathfindAndMoveTo.InvokeFunc(dest, false))
            {
                Chat.PrintError("vnavmesh が経路を受け付けませんでした。メッシュの準備を待ってください。");
                return;
            }
        }
        catch (IpcNotReadyError)
        {
            Chat.PrintError("vnavmesh が入っていないか、まだ準備できていません。");
            return;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "vnavmesh IPC failed");
            Chat.PrintError("プレイヤー移動に失敗しました。vnavmesh を確認してください。");
            return;
        }

        Chat.Print($"自分を ({Fmt(dest)}) へ歩かせます。");
        Log.Information("Player walk -> {X:0.##} {Y:0.##} {Z:0.##}", dest.X, dest.Y, dest.Z);
    }

    private static void StopPlayer()
    {
        CommandManager.ProcessCommand("/vnav stop");
        Chat.Print("自分の移動を止めました。");
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

        if (parts.Length >= 1 && IsToken(parts[0], "object", "オブジェクト"))
        {
            if (parts.Length < 2)
            {
                error = "object のあとには EntityId が必要です。 /cmove list で確認できます。";
                return false;
            }

            return TryFindObject(parts[1], out var obj, out error) && Set(obj.Position, out dest);
        }

        if (parts.Length >= 1 && IsToken(parts[0], "offset", "相対"))
        {
            if (parts.Length < 5)
            {
                error = "offset のあとには 原点 dx dy dz が必要です。例: /cmove player offset target 5 0 -3";
                return false;
            }

            if (!TryFindObject(parts[1], out var origin, out error))
                return false;
            if (!TryParseXyz(parts[2], parts[3], parts[4], out var rel))
            {
                error = "相対座標が数字ではありません。例: 5 0 -3";
                return false;
            }

            dest = origin.Position + rel;
            return true;
        }

        if (parts.Length == 1 && IsToken(parts[0], "here", "ここ"))
            return TryPlayerPosition(out dest, out error);

        if (parts.Length == 1 && IsToken(parts[0], "target", "ターゲット"))
            return TryTargetPosition(out dest, out error);

        if (parts.Length == 1 && IsToken(parts[0], "cursor", "カーソル"))
        {
            if (TryGetCursorGround(out dest))
                return true;
            error = "カーソルの下に地面がありません。";
            return false;
        }

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

        if (parts.Length == 2 && IsToken(parts[1], "cursor", "カーソル"))
        {
            requiredName = parts[0];
            if (TryGetCursorGround(out dest))
                return true;
            error = "カーソルの下に地面がありません。";
            return false;
        }

        if (parts.Length >= 4 && TryParseXyz(parts[1], parts[2], parts[3], out dest))
        {
            requiredName = parts[0];
            return true;
        }

        if (parts.Length >= 3 && TryParseXyz(parts[0], parts[1], parts[2], out dest))
            return true;

        error = "行き先が正しくありません。 /cmove  で使い方を見てください。";
        return false;
    }

    private static bool TryFindObject(string token, out IGameObject obj, out string error)
    {
        obj = null!;
        error = string.Empty;

        if (IsToken(token, "target", "ターゲット"))
            return TryGetTarget(out obj, out error);

        if (IsToken(token, "me", "自分", "here", "ここ"))
        {
            if (Objects.LocalPlayer is { } me)
            {
                obj = me;
                return true;
            }

            error = "自分の座標が取れません。";
            return false;
        }

        if (uint.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var entityId) &&
            entityId != 0)
        {
            var found = Objects.SearchByEntityId(entityId);
            if (found != null)
            {
                obj = found;
                return true;
            }

            error = $"EntityId {entityId} の対象は見つかりません。 /cmove list で確認してください。";
            return false;
        }

        if (ulong.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var gameObjectId))
        {
            var found = Objects.SearchById(gameObjectId);
            if (found != null)
            {
                obj = found;
                return true;
            }
        }

        error = $"「{token}」はターゲット名でも EntityId でもありません。";
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
        if (!TryGetTarget(out var target, out error))
            return false;

        dest = target.Position;
        return true;
    }

    private static bool TryGetTarget(out IGameObject obj, out string error)
    {
        obj = null!;
        error = string.Empty;
        if (Targets.Target is not { } target)
        {
            error = "ターゲットがいません。原点にしたい相手を選んでください。";
            return false;
        }

        obj = target;
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

    private static bool Set(Vector3 value, out Vector3 dest)
    {
        dest = value;
        return true;
    }

    private static bool IsPet(string who) => IsToken(who, "pet", "ペット");

    private static string[] SplitArgs(string args)
        => (args ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);

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
