using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace kinatraa.PlayerPrefEditor
{
    /// <summary>
    /// Undo/redo for PlayerPrefs changes. Every change made through <see cref="Commit"/> records the previous state of
    /// the keys it touches. History lasts for the Editor session and survives script reloads.
    /// </summary>
    public sealed class PrefHistory : ScriptableSingleton<PrefHistory>
    {
        [Serializable]
        class Step
        {
            public string Label;
            public string Writes; // PrefJson document
            public List<string> Deletes = new List<string>();
        }

        const int Limit = 100;

        [SerializeField] List<Step> _undo = new List<Step>();
        [SerializeField] List<Step> _redo = new List<Step>();

        /// <summary>Raised after any commit, undo or redo.</summary>
        public static event Action Changed;

        public bool CanUndo => _undo.Count > 0;
        public bool CanRedo => _redo.Count > 0;
        public string UndoLabel => CanUndo ? _undo[_undo.Count - 1].Label : null;
        public string RedoLabel => CanRedo ? _redo[_redo.Count - 1].Label : null;

        /// <summary>Deletes, writes and saves (see <see cref="PlayerPrefStore.Commit"/>) as one undoable step.</summary>
        public void Commit(string label, IReadOnlyCollection<PrefEntry> writes, IReadOnlyCollection<string> deletes)
        {
            var inverse = Capture(label, writes.Select(e => e.Key).Concat(deletes));
            PlayerPrefStore.Commit(writes, deletes); // throws before changing anything, so nothing is recorded
            Push(_undo, inverse);
            _redo.Clear();
            Changed?.Invoke();
        }

        public void Commit(string label, PrefEntry write) => Commit(label, new[] { write }, Array.Empty<string>());

        /// <summary>Reverts the last step; returns its label, or null when there is nothing to undo.</summary>
        public string Undo() => Move(_undo, _redo);

        /// <summary>Re-applies the last undone step; returns its label, or null when there is nothing to redo.</summary>
        public string Redo() => Move(_redo, _undo);

        public void Clear()
        {
            _undo.Clear();
            _redo.Clear();
            Changed?.Invoke();
        }

        string Move(List<Step> from, List<Step> to)
        {
            if (from.Count == 0) return null;
            var step = from[from.Count - 1];
            from.RemoveAt(from.Count - 1);
            PrefJson.TryParseDocument(step.Writes, out var writes, out _);
            var inverse = Capture(step.Label, writes.Select(e => e.Key).Concat(step.Deletes));
            PlayerPrefStore.Commit(writes, step.Deletes);
            Push(to, inverse);
            Changed?.Invoke();
            return step.Label;
        }

        static Step Capture(string label, IEnumerable<string> keys)
        {
            var step = new Step { Label = label };
            var writes = new List<PrefEntry>();
            foreach (var key in keys.Distinct())
            {
                var current = PlayerPrefStore.Read(key);
                if (current == null) step.Deletes.Add(key);
                // ponytail: a key of unknown type can't be read back, so its old value can't be restored.
                else if (current.Type != PrefType.Unknown) writes.Add(current);
            }
            step.Writes = PrefJson.FormatDocument(writes);
            return step;
        }

        static void Push(List<Step> stack, Step step)
        {
            stack.Add(step);
            if (stack.Count > Limit) stack.RemoveAt(0);
        }
    }
}
