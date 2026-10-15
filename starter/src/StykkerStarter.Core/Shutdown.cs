namespace StykkerStarter.Core;

// Das Beenden in fester Reihenfolge. Jeder Schritt wird im Protokoll festgehalten, wenn er fertig ist. Ein Schritt,
// der wirft, bricht das Beenden nicht ab: die übrigen laufen trotzdem, sonst bliebe der Rest des Starters stehen.
public static class Shutdown
{
    public static void Run(StarterLog? log, IEnumerable<(string Name, Action Step)> steps)
    {
        foreach (var (name, step) in steps)
        {
            try
            {
                step();
                log?.Write($"quit: {name}");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                log?.Write($"quit: {name} failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
