using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace ArenaBoxBot;

// Reopen only an unsubmitted native binding error after its exact original
// entities, positions, card identities and all requested attributes agree.
// The original Evaluate must still perform every validation and decision.
internal static class BgPreAdjustmentRecovery
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly Dictionary<Type, Contract> Contracts = new Dictionary<Type, Contract>();

    private sealed class Contract
    {
        internal readonly FieldInfo Key, Turn, Cursor, Error, Expected, Identities, Pending, Proposed;
        internal readonly MethodInfo Reset;
        internal Contract(Type type)
        {
            Key = Field(type, "key"); Turn = Field(type, "turn"); Cursor = Field(type, "cursor");
            Error = Field(type, "error"); Expected = Field(type, "expected"); Identities = Field(type, "identities");
            Pending = Field(type, "pending"); Proposed = Field(type, "proposed");
            Reset = type.GetMethod("Reset", Members, null, Type.EmptyTypes, null);
            if (Reset == null || Reset.ReturnType != typeof(void)) throw new MissingMethodException("Reset");
        }
    }

    internal static bool TryReset(object evaluator, string requestKey, int localTurn,
        object movesValue, object boardValue, out string detail)
    {
        detail = null;
        try
        {
            if (evaluator == null || string.IsNullOrEmpty(requestKey) || localTurn < 1 ||
                evaluator.GetType().FullName != "HsMod.Utilities.BattlegroundsPreAdjustment") return false;
            Contract c;
            lock (Contracts)
            {
                if (!Contracts.TryGetValue(evaluator.GetType(), out c))
                    Contracts[evaluator.GetType()] = c = new Contract(evaluator.GetType());
            }
            if (!Equals(c.Error.GetValue(evaluator), "move_identity_mismatch") ||
                !Equals(c.Key.GetValue(evaluator), requestKey) ||
                !Equals(c.Turn.GetValue(evaluator), localTurn) || !Equals(c.Cursor.GetValue(evaluator), 0) ||
                c.Pending.GetValue(evaluator) != null || c.Proposed.GetValue(evaluator) != null) return false;

            var expected = c.Expected.GetValue(evaluator) as int[];
            var identities = c.Identities.GetValue(evaluator) as IDictionary;
            var board = Items(boardValue).OrderBy(v => Int(v, "Position")).ToArray();
            var moves = Items(movesValue).ToArray();
            if (expected == null || identities == null || board.Length < 1 || board.Length > 7 ||
                expected.Length != board.Length || identities.Count != board.Length || moves.Length < 1 || moves.Length > 7)
                return false;
            var entityIds = new HashSet<int>();
            for (var i = 0; i < board.Length; i++)
            {
                var id = Int(board[i], "EntityId"); var card = Text(board[i], "CardId");
                if (id <= 0 || !entityIds.Add(id) || Int(board[i], "Position") != i + 1 ||
                    expected[i] != id || string.IsNullOrEmpty(card) || !identities.Contains(id) ||
                    !string.Equals(identities[id] as string, card, StringComparison.OrdinalIgnoreCase)) return false;
            }
            foreach (var move in moves)
            {
                var to = Int(move, "MoveTo"); var id = Int(move, "SourceEntityId");
                var from = Int(move, "SourcePosition"); var card = Text(move, "SourceCardId");
                if (to < 1 || to > board.Length || id < 0 || string.IsNullOrEmpty(card)) return false;
                var source = id > 0 ? board.FirstOrDefault(v => Int(v, "EntityId") == id) :
                    board.FirstOrDefault(v => Int(v, "Position") == from);
                if (source == null || !string.Equals(Text(source, "CardId"), card, StringComparison.OrdinalIgnoreCase) ||
                    !MatchesOptional(move, "Attack", Int(source, "Attack")) ||
                    !MatchesOptional(move, "Health", Int(source, "Health"))) return false;
            }
            // No native move has been submitted by this failed binding. Do not
            // change arguments, invent Ready, or release an unconfirmed move.
            c.Reset.Invoke(evaluator, null);
            detail = "turn=" + localTurn + "; moves=" + moves.Length + "; exact original board and requested attributes now match";
            return true;
        }
        catch { return false; } // Unknown native contracts keep their original path.
    }

    private static FieldInfo Field(Type type, string name) => type.GetField(name, Members) ?? throw new MissingFieldException(type.FullName, name);
    private static object Read(object value, string name)
    {
        if (value == null) throw new ArgumentNullException(nameof(value));
        var type = value.GetType(); var field = type.GetField(name, Members);
        if (field != null) return field.GetValue(value);
        var property = type.GetProperty(name, Members);
        if (property == null || !property.CanRead || property.GetIndexParameters().Length != 0) throw new MissingMemberException(type.FullName, name);
        return property.GetValue(value, null);
    }
    private static int Int(object value, string name) => (int)Read(value, name);
    private static string Text(object value, string name) => (string)Read(value, name);
    private static bool MatchesOptional(object value, string name, int actual)
    {
        var expected = Read(value, name);
        return expected == null || expected is int number && number == actual;
    }
    private static IEnumerable<object> Items(object value)
    {
        if (!(value is IEnumerable values)) throw new ArgumentException("Missing native list");
        var count = 0;
        foreach (var item in values)
        {
            if (++count > 7 || item == null) throw new ArgumentException("Invalid native list");
            yield return item;
        }
    }
}
