using Wsm.Domain.Exceptions;

namespace Wsm.Domain.ValueObjects;

/// <summary>
/// An application offered on the sandbox launch list (the GUI's quick-launch
/// row): the guest program a RemoteApp (RAIL) session starts, plus an optional
/// command line.
/// <para>
/// Applications are user supplied — a global list lives in the UI settings and
/// each sandbox may carry its own additions (<see cref="SandboxRecipe.Apps"/>),
/// so a portable <c>.wsm</c> brings its applications along.
/// </para>
/// </summary>
public sealed record RailApp
{
    public RailApp(string name, string program, string? arguments = null)
    {
        if (string.IsNullOrWhiteSpace(program))
        {
            throw new SandboxValidationException("Application program must not be empty.");
        }

        Name = string.IsNullOrWhiteSpace(name) ? program.Trim() : name.Trim();
        Program = program.Trim();
        Arguments = string.IsNullOrWhiteSpace(arguments) ? null : arguments.Trim();
    }

    /// <summary>The display name shown on the launch button.</summary>
    public string Name { get; init; }

    /// <summary>The guest-side executable, e.g. <c>C:\Windows\System32\notepad.exe</c>.</summary>
    public string Program { get; init; }

    /// <summary>Optional command line for <see cref="Program"/>.</summary>
    public string? Arguments { get; init; }

    public override string ToString() => Name;
}
