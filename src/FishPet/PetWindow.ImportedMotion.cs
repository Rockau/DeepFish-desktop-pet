namespace FishPet;

internal sealed partial class PetWindow
{
    private int? importedCombinationToken;
    private bool PlayingImportedCombination => importedCombinationToken == mutterToken;

    private bool TryPlayImportedCombination(string cue, int side, bool plainFace)
    {
        if (!ImportedActions.Combinations.TryGetValue(cue, out var moves)) return false;
        if (ImportedCombinationBlocked) return true;
        int token = ++mutterToken;
        importedCombinationToken = token;
        _ = PlayImportedCombination(moves, side, token, plainFace);
        return true;
    }

    private bool ImportedCombinationBlocked => exiting || standby || hiding || hiddenForFullscreen ||
        dragging || rightDragging || stroking || dizzyOn || speech.IsSaying || speech.IsShowingReply || silent != null;

    private async Task PlayImportedCombination(string[] moves, int side, int token, bool plainFace)
    {
        var renderer = player;
        long shapeVersion = shapeChange;
        try
        {
            foreach (var move in moves)
            {
                if (token != mutterToken || ImportedCombinationBlocked || renderer != player || shapeVersion != shapeChange)
                    return;

                behavior.Show(move, DateTime.UtcNow, side, force: true, plainFace: plainFace);
                int revision = behavior.MotionRevision;
                await Task.Delay(TimeSpan.FromSeconds(behavior.Action(move).Seconds));

                // Natural expiry keeps the same revision. An interrupt or a later reaction does not.
                if (behavior.MotionRevision != revision) return;
            }
        }
        finally
        {
            if (importedCombinationToken == token) importedCombinationToken = null;
        }
    }
}
