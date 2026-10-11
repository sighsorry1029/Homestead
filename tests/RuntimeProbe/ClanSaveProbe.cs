using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using Splatform;
using UnityEngine;

public sealed partial class Probe
{
    private void CheckClanSaveAccess()
    {
        Check(ZNet.instance && ZNet.instance.IsServer() && ZNetScene.instance, "authoritative world loaded for Clan fixture");
        Assembly clan = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Clan");
        Type access = mod.GetType("Homestead.ZoneBlueprintSaveAccess");
        Check((access.GetField("_resolvePrimary", Any).GetValue(null) != null) == (clan != null), "optional Clan API binding matches loaded plugins");
        Type config = mod.GetType("Homestead.BlueprintConfig");
        var mode = (ConfigEntryBase)config.GetField("_areaSaveCreatorMode", Any).GetValue(null);
        object oldMode = mode.BoxedValue;
        Check(Enum.GetNames(oldMode.GetType()).SequenceEqual(new[] { "AllCreators", "OwnedAndCreatorless", "OwnedOnly" }), "three enum names/order unchanged");
        PlatformUserID actor = Player.m_localPlayer ? UserInfo.GetLocalUser().UserId : new PlatformUserID("Steam", "76561198000009901");
        long actorId = Player.m_localPlayer ? Player.m_localPlayer.GetPlayerID() : 9901L;
        PlatformUserID builder = new PlatformUserID("Steam", "76561198000009902");
        int historyCount = ZNet.World.m_playerHistory.Count;
        ZNet.World.m_playerHistory.Add(new ZNet.CrossNetworkUserInfo { m_id = builder });
        List<ZDO> pieces = new();
        Check((bool)Call("ZoneBlueprintCommands", "HasBuildRecipe", ZNetScene.instance.GetPrefab("woodwall")), "fixture uses registered vanilla building piece");
        ZDO Piece(long creator, int index)
        {
            ZDO zdo = ZDOMan.instance.CreateNewZDO(new Vector3(pieces.Count * 2f, 5000f, 0f), "woodwall".GetStableHashCode());
            zdo.SetPrefab("woodwall".GetStableHashCode());
            zdo.Set(ZDOVars.s_creator, creator);
            zdo.Set(ZDOVars.s_creatorIndex, index);
            pieces.Add(zdo);
            return zdo;
        }
        ZDO own = Piece(actorId, -1), memberPiece = Piece(9902, historyCount), creatorless = Piece(0, -1);
        HashSet<(long, int)> Grants(PlatformUserID platform, long playerId) =>
            (HashSet<(long, int)>)Call("ZoneBlueprintSaveAccess", "Evaluate", platform, playerId, pieces.Select(p => p.m_uid).ToList());
        void Mode(string value) => mode.BoxedValue = Enum.Parse(oldMode.GetType(), value);
        bool Base(long creator) => (bool)Call("BlueprintConfig", "AreaSaveAllowsCreator", actorId, creator);
        try
        {
            Mode("AllCreators");
            Check(Base(actorId) && Base(0) && Base(9902), "AllCreators retains unrestricted creator policy");
            Mode("OwnedOnly");
            Check(Base(actorId) && !Base(0) && !Base(9902), "OwnedOnly retains own pieces without Clan grant");
            Mode("OwnedAndCreatorless");
            Check(Base(actorId) && Base(0) && !Base(9902), "OwnedAndCreatorless retains creatorless fallback");
            Mode("OwnedOnly");
            if (clan == null)
            {
                Check(Grants(actor, actorId).Count == 0, "missing Clan grants no additional creators");
                if (Player.m_localPlayer) CheckClanSavePipeline(own, memberPiece, 1);
                return;
            }

            Type registry = clan.GetType("Clan.ClanRegistry");
            AccessTools.Method(registry, "EnsureLoaded").Invoke(null, null);
            Type playerRef = clan.GetType("Clan.ClanPlayerRef"), stateType = clan.GetType("Clan.ClanState"), memberType = clan.GetType("Clan.ClanMember");
            object stateA = Activator.CreateInstance(stateType, new object[] { new string('a', 32) });
            object stateB = Activator.CreateInstance(stateType, new object[] { new string('b', 32) });
            stateType.GetField("Name").SetValue(stateA, "Probe A"); stateType.GetField("Name").SetValue(stateB, "Probe B");
            object Add(object state, PlatformUserID platform, long id, string role)
            {
                object identity = Activator.CreateInstance(playerRef, new object[] { platform.ToString(), id, "Probe" });
                object member = Activator.CreateInstance(memberType);
                memberType.GetField("Player").SetValue(member, identity);
                memberType.GetField("Role").SetValue(member, Enum.Parse(clan.GetType("Clan.ClanRole"), role));
                ((IDictionary)stateType.GetField("Members").GetValue(state)).Add(playerRef.GetField("Id").GetValue(identity), member);
                return member;
            }
            object actorMember = Add(stateA, actor, actorId, "Leader");
            object offlineMember = Add(stateA, builder, 9902, "Member");
            object guestMember = Add(stateB, builder, 9902, "Guest");
            Add(stateB, actor, actorId, "Guest");
            void Publish()
            {
                Type dataType = registry.GetNestedType("RegistryData", Any);
                object data = Activator.CreateInstance(dataType, true);
                foreach (object state in new[] { stateA, stateB })
                {
                    ((IDictionary)dataType.GetField("ClansById").GetValue(data)).Add(stateType.GetProperty("ClanId").GetValue(state), state);
                    ((IDictionary)dataType.GetField("ClansByName").GetValue(data)).Add(stateType.GetField("Name").GetValue(state), state);
                    foreach (DictionaryEntry entry in (IDictionary)stateType.GetField("Members").GetValue(state))
                    {
                        string index = memberType.GetField("Role").GetValue(entry.Value).ToString() == "Guest" ? "GuestClanByPlayerId" : "PrimaryClanByPlayerId";
                        ((IDictionary)dataType.GetField(index).GetValue(data)).Add(entry.Key, state);
                    }
                }
                AccessTools.Method(registry, "SwapState").Invoke(null, new[] { data });
            }
            var roleField = memberType.GetField("Role");
            foreach (string role in new[] { "Leader", "Officer", "Member" })
            {
                roleField.SetValue(offlineMember, Enum.Parse(roleField.FieldType, role));
                Publish();
                Check(Grants(actor, actorId).Contains((9902L, historyCount)), "offline Primary " + role + " allowed despite both players having another Guest link");
            }
            Check(Grants(builder, actorId).Count == 0, "same character ID with wrong platform cannot borrow requester membership");
            memberPiece.Set(ZDOVars.s_creatorIndex, -1);
            Check(Grants(actor, actorId).Count == 0, "missing creator history denies extra allowance");
            memberPiece.Set(ZDOVars.s_creatorIndex, ZNet.World.m_playerHistory.Count);
            Check(Grants(actor, actorId).Count == 0, "out-of-range creator history denies extra allowance");
            memberPiece.Set(ZDOVars.s_creatorIndex, historyCount);
            memberPiece.Set(ZDOVars.s_creator, 9999L);
            Check(Grants(actor, actorId).Count == 0, "different character on member platform is not a member");
            memberPiece.Set(ZDOVars.s_creator, 9902L);
            if (Player.m_localPlayer) CheckClanSavePipeline(own, memberPiece, 2);

            // Remove Primary membership but keep the Guest link: selection-time grants
            // must not survive this authoritative registry change at the Save button.
            IDictionary membersA = (IDictionary)stateType.GetField("Members").GetValue(stateA);
            string memberId = (string)playerRef.GetField("Id").GetValue(memberType.GetField("Player").GetValue(offlineMember));
            membersA.Remove(memberId);
            Publish();
            Check(Grants(actor, actorId).Count == 0, "Guest-only creator denied after leaving Primary clan");
            if (Player.m_localPlayer) CheckClanSavePipeline(own, memberPiece, 1);
            membersA.Add(memberId, offlineMember);
            // Simulate an unresolved/unknown role without allowing it through the index.
            roleField.SetValue(offlineMember, Enum.ToObject(roleField.FieldType, 99));
            Publish();
            Check(Grants(actor, actorId).Count == 0, "unknown role fails closed");
            roleField.SetValue(offlineMember, Enum.Parse(roleField.FieldType, "Member"));
            Publish();
            if (Player.m_localPlayer) CheckClanReplyLifecycle(memberPiece, mode);
            Check(memberPiece.GetLong(ZDOVars.s_creator) == 9902 && memberPiece.GetInt(ZDOVars.s_creatorIndex) == historyCount, "Area Save checks never rewrite creator identity");
        }
        finally
        {
            mode.BoxedValue = oldMode;
            Call("ZoneBlueprintSaveAccess", "ResetForWorldSession");
            foreach (ZDO piece in pieces) ZDOMan.instance.DestroyZDO(piece);
            ZNet.World.m_playerHistory.RemoveRange(historyCount, ZNet.World.m_playerHistory.Count - historyCount);
        }
    }

    private void CheckClanSavePipeline(ZDO own, ZDO member, int expectedPieces)
    {
        Type toolType = mod.GetType("Homestead.ZoneBlueprintSaveTool");
        AccessTools.Method(toolType, "EnsureInstance").Invoke(null, null);
        object tool = toolType.GetField("_instance", Any).GetValue(null);
        Type selectionType = toolType.GetNestedType("SaveSelection", Any);
        object selection = Activator.CreateInstance(selectionType, new object[] { own.GetPosition(), Quaternion.identity, 8f, new List<ZDOID> { own.m_uid, member.m_uid } });
        toolType.GetField("_selection", Any).SetValue(tool, selection);
        string name = "clan_probe_" + Guid.NewGuid().ToString("N");
        toolType.GetField("_saveName", Any).SetValue(tool, name);
        AccessTools.Method(toolType, "SaveSelectionFromUi").Invoke(tool, null);
        Check(toolType.GetField("_selection", Any).GetValue(tool) == null, "Save completed: " + toolType.GetField("_saveStatus", Any).GetValue(tool));
        object blueprint = Call("ZoneBlueprintCommands", "LoadBlueprint", name);
        Check(((IList)blueprint.GetType().GetProperty("Entries").GetValue(blueprint)).Count == expectedPieces,
            "Save button rechecks current membership and writes " + expectedPieces + " allowed pieces");
        Check(!(bool)toolType.GetField("_savePending", Any).GetValue(tool), "Save button releases pending state");
    }

    private static bool ClanPretendClient(ref bool __result) { __result = false; return false; }
    private static ZNetPeer clanFixtureServer;
    private static bool ClanFixtureServer(ref ZNetPeer __result) { __result = clanFixtureServer; return false; }

    private void CheckClanReplyLifecycle(ZDO member, ConfigEntryBase mode)
    {
        Type access = mod.GetType("Homestead.ZoneBlueprintSaveAccess");
        Type pendingType = access.GetNestedType("Pending", Any);
        var key = (member.GetLong(ZDOVars.s_creator), member.GetInt(ZDOVars.s_creatorIndex));
        int calls = 0, granted = -1;
        void Pending(long id)
        {
            object pending = Activator.CreateInstance(pendingType, true);
            pendingType.GetField("Id").SetValue(pending, id);
            pendingType.GetField("Deadline").SetValue(pending, Time.realtimeSinceStartup + 5f);
            pendingType.GetField("Player").SetValue(pending, Player.m_localPlayer);
            pendingType.GetField("Mode").SetValue(pending, mode.BoxedValue);
            pendingType.GetField("Creators").SetValue(pending, new Dictionary<(long, int), ZDOID> { [key] = member.m_uid });
            pendingType.GetField("Complete").SetValue(pending, new Action<HashSet<(long, int)>>(grants => { calls++; granted = grants.Count; }));
            access.GetField("_selectionRequest", Any).SetValue(null, pending);
            access.GetField("_player", Any).SetValue(null, Player.m_localPlayer);
        }
        ZPackage Reply(long id, (long, int) identity)
        {
            ZPackage package = new(); package.Write(id); package.Write(1); package.Write(identity.Item1); package.Write(identity.Item2); package.SetPos(0); return package;
        }
        Harmony fixture = new("homestead.clan.fixture");
        fixture.Patch(AccessTools.Method(typeof(ZNet), "IsServer"), prefix: new HarmonyMethod(typeof(Probe), nameof(ClanPretendClient)));
        clanFixtureServer = (ZNetPeer)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(ZNetPeer));
        clanFixtureServer.m_uid = 12345678;
        fixture.Patch(AccessTools.Method(typeof(ZNet), "GetServerPeer"), prefix: new HarmonyMethod(typeof(Probe), nameof(ClanFixtureServer)));
        try
        {
            long server = clanFixtureServer.m_uid;
            Pending(101);
            Call("ZoneBlueprintSaveAccess", "OnResponse", server + 1, Reply(101, key));
            Check(calls == 0, "non-server response ignored");
            Call("ZoneBlueprintSaveAccess", "OnResponse", server, Reply(100, key));
            Check(calls == 0, "stale request response ignored");
            Call("ZoneBlueprintSaveAccess", "OnResponse", server, Reply(101, (99999L, key.Item2)));
            Check(calls == 0, "unrequested creator response ignored");
            Call("ZoneBlueprintSaveAccess", "OnResponse", server, Reply(101, key));
            Check(calls == 1 && granted == 1, "matching server response completes once");
            Call("ZoneBlueprintSaveAccess", "OnResponse", server, Reply(101, key));
            Check(calls == 1, "duplicate response ignored");
            Pending(102);
            object pending = access.GetField("_selectionRequest", Any).GetValue(null);
            pendingType.GetField("Deadline").SetValue(pending, -1f);
            Call("ZoneBlueprintSaveAccess", "Update");
            Check(calls == 2 && granted == 0, "timeout preserves base policy without Clan grants");
            Pending(103);
            mode.BoxedValue = Enum.Parse(mode.BoxedValue.GetType(), "OwnedAndCreatorless");
            Call("ZoneBlueprintSaveAccess", "OnResponse", server, Reply(103, key));
            Check(calls == 3 && granted == 0, "live config change invalidates in-flight Clan grants");
            Pending(104);
            Call("ZoneBlueprintSaveAccess", "CancelSelection");
            Call("ZoneBlueprintSaveAccess", "OnResponse", server, Reply(104, key));
            Check(calls == 3, "canceled selection ignores delayed response");
            Pending(105);
            Call("ZoneBlueprintSaveAccess", "ResetForWorldSession");
            Call("ZoneBlueprintSaveAccess", "OnResponse", server, Reply(105, key));
            Check(calls == 3, "world/session reset ignores delayed response");
        }
        finally { fixture.UnpatchSelf(); }
    }
}
