using System.Globalization;
using System.Xml.Linq;
using Volo.Abp.DependencyInjection;
using Wsm.Domain.ValueObjects;

namespace Wsm.Recipe.Serialization.Internal;

/// <summary>
/// Serializes/deserializes the Windows Sandbox <c>.wsb</c> recipe. The XML shape
/// matches Microsoft's <c>WsbRecipe</c> type exactly (root <c>Configuration</c>,
/// the same element names/order, and <c>Enable</c>/<c>Disable</c> for the
/// tri-state flags) so the payload can be sent straight to the gRPC server or
/// written to a file that <c>WindowsSandbox.exe</c> understands.
/// </summary>
[ExposeServices(typeof(ISandboxRecipeSerializer))]
internal sealed class WsbRecipeXmlSerializer : ISandboxRecipeSerializer, ITransientDependency
{
    public string Serialize(SandboxRecipe recipe)
    {
        var root = new XElement("Configuration");
        root.Add(new XElement("DisplayName", recipe.Name.Value));

        if (recipe.MappedFolders.Count > 0)
        {
            var folders = new XElement("MappedFolders");
            foreach (var folder in recipe.MappedFolders)
            {
                var element = new XElement("MappedFolder",
                    new XElement("HostFolder", folder.HostFolder));

                if (!string.IsNullOrEmpty(folder.SandboxFolder))
                {
                    element.Add(new XElement("SandboxFolder", folder.SandboxFolder));
                }

                element.Add(new XElement("ReadOnly", folder.ReadOnly ? "true" : "false"));
                folders.Add(element);
            }

            root.Add(folders);
        }

        if (recipe.LogonCommands.Count > 0)
        {
            var logon = new XElement("LogonCommand");
            foreach (var command in recipe.LogonCommands)
            {
                logon.Add(new XElement("Command", command.Command));
            }

            root.Add(logon);
        }

        AddTriStateFlag(root, "vGPU", recipe.VirtualGpu);
        AddTriStateFlag(root, "Networking", recipe.Networking);
        AddBool(root, "AudioInput", recipe.Redirections.AudioInput);
        AddBool(root, "VideoInput", recipe.Redirections.VideoInput);
        AddBool(root, "PrinterRedirection", recipe.Redirections.Printer);
        AddBool(root, "ClipboardRedirection", recipe.Redirections.Clipboard);
        root.Add(new XElement("MemoryInMB", recipe.Memory.MemoryMb.ToString(CultureInfo.InvariantCulture)));
        AddBool(root, "ProtectedClient", recipe.ProtectedClient);
        AddBool(root, "SmartCardRedirection", recipe.Redirections.SmartCard);
        AddBool(root, "FullScreenMode", recipe.FullScreen);
        root.Add(new XElement("RdpTransport", recipe.Transport.ToString()));
        AddBool(root, "RailMode", recipe.RailMode);
        root.Add(new XElement("TargetDisplay", "0"));
        AddBool(root, "RebootSupport", recipe.RebootSupport);
        AddBool(root, "PersistenceSupport", recipe.PersistenceSupport);

        if (!string.IsNullOrEmpty(recipe.AccountUser))
        {
            root.Add(new XElement("AccountUser", recipe.AccountUser));
        }

        if (!string.IsNullOrEmpty(recipe.AccountPassword))
        {
            root.Add(new XElement("AccountPassword", recipe.AccountPassword));
        }

        // The container desktop plan is a WSM extension (it configures containers, which
        // do not use this .wsb payload at all). It is only written when a recipe actually
        // asks for something, so every existing recipe keeps producing exactly the payload
        // Microsoft's WsbRecipe type understands.
        if (recipe.Desktop != SandboxDesktopPlan.Default)
        {
            var desktop = new XElement("ContainerDesktop", new XElement("Kind", recipe.Desktop.Kind.ToString()));
            if (!string.IsNullOrWhiteSpace(recipe.Desktop.Provider))
            {
                desktop.Add(new XElement("Provider", recipe.Desktop.Provider));
            }

            AddBool(desktop, "InteractiveSession", recipe.Desktop.InteractiveSession);
            AddBool(desktop, "PresentToInteractiveSession", recipe.Desktop.PresentToInteractiveSession);
            AddBool(desktop, "DirtyRegions", recipe.Desktop.DirtyRegions);
            if (recipe.Desktop.Width > 0)
            {
                desktop.Add(new XElement("Width", recipe.Desktop.Width.ToString(CultureInfo.InvariantCulture)));
            }

            if (recipe.Desktop.Height > 0)
            {
                desktop.Add(new XElement("Height", recipe.Desktop.Height.ToString(CultureInfo.InvariantCulture)));
            }

            if (recipe.Desktop.Fps > 0)
            {
                desktop.Add(new XElement("Fps", recipe.Desktop.Fps.ToString(CultureInfo.InvariantCulture)));
            }

            root.Add(desktop);
        }

        if (!string.IsNullOrWhiteSpace(recipe.ContainerImagePath))
        {
            root.Add(new XElement("ContainerImagePath", recipe.ContainerImagePath));
        }

        return root.ToString(SaveOptions.DisableFormatting);
    }

    public SandboxRecipe Deserialize(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return SandboxRecipe.Default;
        }

        var root = XDocument.Parse(payload).Root
                   ?? throw new FormatException("The .wsb recipe has no root element.");

        var folders = root.Element("MappedFolders")?.Elements("MappedFolder")
            .Select(ParseFolder)
            .ToArray() ?? Array.Empty<MappedFolder>();

        var commands = root.Element("LogonCommand")?.Elements("Command")
            .Select(c => new LogonCommand(c.Value))
            .ToArray() ?? Array.Empty<LogonCommand>();

        var redirections = new SandboxRedirections
        {
            AudioInput = ReadBool(root, "AudioInput") ?? true,
            VideoInput = ReadBool(root, "VideoInput") ?? false,
            Printer = ReadBool(root, "PrinterRedirection") ?? false,
            Clipboard = ReadBool(root, "ClipboardRedirection") ?? true,
            SmartCard = ReadBool(root, "SmartCardRedirection") ?? false,
        };

        return new SandboxRecipe
        {
            Name = new SandboxName(root.Element("DisplayName")?.Value is { Length: > 0 } n ? n : SandboxName.DefaultValue),
            Memory = new SandboxMemoryConfiguration(ReadInt(root, "MemoryInMB") ?? SandboxMemoryConfiguration.DefaultMemoryMb),
            Networking = ReadTriState<NetworkingMode>(root, "Networking"),
            VirtualGpu = ReadTriState<VirtualGpuMode>(root, "vGPU"),
            Redirections = redirections,
            Transport = ReadEnum(root, "RdpTransport", RdpTransportKind.NamedPipe),
            RailMode = ReadBool(root, "RailMode") ?? false,
            FullScreen = ReadBool(root, "FullScreenMode") ?? false,
            ProtectedClient = ReadBool(root, "ProtectedClient") ?? false,
            RebootSupport = ReadBool(root, "RebootSupport") ?? false,
            PersistenceSupport = ReadBool(root, "PersistenceSupport") ?? false,
            MappedFolders = folders,
            LogonCommands = commands,
            AccountUser = root.Element("AccountUser")?.Value,
            AccountPassword = root.Element("AccountPassword")?.Value,
            Desktop = ParseDesktop(root.Element("ContainerDesktop")),
            ContainerImagePath = root.Element("ContainerImagePath")?.Value,
        };
    }

    /// <summary>Reads the WSM container desktop extension element, if the recipe has one.</summary>
    private static SandboxDesktopPlan ParseDesktop(XElement? element)
    {
        if (element is null)
        {
            return SandboxDesktopPlan.Default;
        }

        return new SandboxDesktopPlan
        {
            Kind = ReadEnum(element, "Kind", ContainerDesktopKind.FrameBuffer),
            InteractiveSession = ReadBool(element, "InteractiveSession") ?? false,
            Provider = element.Element("Provider")?.Value,
            PresentToInteractiveSession = ReadBool(element, "PresentToInteractiveSession") ?? true,
            DirtyRegions = ReadBool(element, "DirtyRegions") ?? true,
            Width = ReadInt(element, "Width") ?? 0,
            Height = ReadInt(element, "Height") ?? 0,
            Fps = ReadInt(element, "Fps") ?? 0,
        };
    }

    private static MappedFolder ParseFolder(XElement element)
    {
        var host = element.Element("HostFolder")?.Value ?? string.Empty;
        var sandbox = element.Element("SandboxFolder")?.Value ?? string.Empty;
        var readOnly = ReadBool(element, "ReadOnly") ?? true;
        return new MappedFolder(host, sandbox, readOnly);
    }

    private static void AddTriStateFlag(XElement root, string name, NetworkingMode mode)
    {
        switch (mode)
        {
            case NetworkingMode.Enabled:
                root.Add(new XElement(name, "Enable"));
                break;
            case NetworkingMode.Disabled:
                root.Add(new XElement(name, "Disable"));
                break;
            case NetworkingMode.Default:
            default:
                break;
        }
    }

    private static void AddTriStateFlag(XElement root, string name, VirtualGpuMode mode)
    {
        switch (mode)
        {
            case VirtualGpuMode.Enabled:
                root.Add(new XElement(name, "Enable"));
                break;
            case VirtualGpuMode.Disabled:
                root.Add(new XElement(name, "Disable"));
                break;
            case VirtualGpuMode.Default:
            default:
                break;
        }
    }

    private static void AddBool(XElement root, string name, bool value)
        => root.Add(new XElement(name, value ? "Enable" : "Disable"));

    private static bool? ReadBool(XElement root, string name)
    {
        var value = root.Element(name)?.Value?.Trim().ToLowerInvariant();
        return value switch
        {
            "enable" or "enabled" or "true" => true,
            "disable" or "disabled" or "false" => false,
            _ => null,
        };
    }

    private static int? ReadInt(XElement root, string name)
        => int.TryParse(root.Element(name)?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    private static TEnum ReadEnum<TEnum>(XElement root, string name, TEnum fallback)
        where TEnum : struct
        => Enum.TryParse<TEnum>(root.Element(name)?.Value, ignoreCase: true, out var value) ? value : fallback;

    private static TEnum ReadTriState<TEnum>(XElement root, string name)
        where TEnum : struct, Enum
    {
        var value = root.Element(name)?.Value?.Trim().ToLowerInvariant();
        var member = value switch
        {
            "enable" or "enabled" or "true" => "Enabled",
            "disable" or "disabled" or "false" => "Disabled",
            _ => "Default",
        };

        return Enum.TryParse<TEnum>(member, ignoreCase: true, out var result) ? result : default;
    }
}
