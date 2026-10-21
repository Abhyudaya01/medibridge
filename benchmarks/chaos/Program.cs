namespace MediBridge.Chaos;

internal static class ChaosEntryPoint
{
    public static Task<int> Main(string[] args) => ChaosProgram.RunAsync(args);
}
