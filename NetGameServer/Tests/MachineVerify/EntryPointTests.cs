using System.Linq;
using System.Reflection;
using Xunit;

namespace MachineVerify;

public class EntryPointTests
{
    [Fact]
    public async Task VerifyMainReturnsSuccess()
    {
        var asm = Assembly.GetExecutingAssembly();
        var type = asm.GetType("Program") ?? asm.GetType("MachineVerify.Program");
        Assert.NotNull(type);

        var method = type!.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(m => string.Equals(m.Name, "Main", StringComparison.Ordinal)
                              || m.Name.Contains("Main", StringComparison.Ordinal));
        Assert.NotNull(method);

        object? result = method!.GetParameters().Length == 0
            ? method.Invoke(null, null)
            : method.Invoke(null, new object?[] { Array.Empty<string>() });

        int exitCode = await ToExitCodeAsync(result);
        Assert.True(exitCode == 0, $"Verify Main 返回非零退出码: {exitCode}");
    }

    private static async Task<int> ToExitCodeAsync(object? result)
    {
        return result switch
        {
            Task<int> t => await t,
            Task t => await AwaitAndZero(t),
            int code => code,
            _ => 0
        };
    }

    private static async Task<int> AwaitAndZero(Task task)
    {
        await task;
        return 0;
    }
}
