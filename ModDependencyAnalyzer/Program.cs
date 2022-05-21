using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Serialization;
using OpenSoftware.DgmlTools;
using OpenSoftware.DgmlTools.Builders;
using OpenSoftware.DgmlTools.Model;
using StardewModdingAPI;
using StardewModdingAPI.Toolkit;
using StardewModdingAPI.Toolkit.Framework.GameScanning;
using StardewModdingAPI.Toolkit.Framework.ModScanning;
using StardewModdingAPI.Toolkit.Serialization.Models;
using StardewModdingAPI.Toolkit.Utilities;

namespace ModDependencyAnalyzer;

/// <summary>The app entry point.</summary>
internal static class Program
{
    /*********
    ** Fields
    *********/
    /// <summary>The absolute or relative path to the generated <c>.dgml</c> file.</summary>
    private const string GeneratedFilePath = "mod-dependencies.dgml";

    /// <summary>Whether to group mods by content pack, instead of adding a dependency link to their consuming mod.</summary>
    private const bool GroupContentPacks = true;


    /*********
    ** Public methods
    *********/
    /// <summary>Run the app logic.</summary>
    public static void Main()
    {
        // get mods
        DirectoryInfo modsFolder = Program.GetModsFolder();
        ModFolder[] mods = Program.GetInstalledMods(modsFolder.FullName).ToArray();

        // export .dgml file
        DirectedGraph graph = Program.BuildDirectedGraph(mods, Program.GroupContentPacks);
        Program.ExportToDgml(graph, Program.GeneratedFilePath);

        // render image
        //Program.ConvertDgmlToPng(Program.GeneratedFilePath);

        // output
        string filePath = Path.Combine(Environment.CurrentDirectory, Program.GeneratedFilePath);
        Console.WriteLine();
        Console.WriteLine($"Generated at {filePath}.");
        if (Program.InteractivelyChoose("Do you want to open the DGML file in its default editor? [y]es [n]o", new[] { "y", "n" }) == "y")
        {
            Process.Start(
                new ProcessStartInfo(filePath)
                {
                    UseShellExecute = true
                }
            );
        }
    }


    /*********
    ** Private methods
    *********/
    /// <summary>Interactively find the game's <c>Mods</c> folder.</summary>
    private static DirectoryInfo GetModsFolder()
    {
        GameScanner gameScanner = new();

        // get detected paths
        DirectoryInfo[] defaultPaths = gameScanner
            .Scan()
            .Select(path => new DirectoryInfo(Path.Combine(path.FullName, "Mods")))
            .ToArray();
        if (defaultPaths.Any())
        {
            Console.WriteLine("Which game mods folder do you want to scan?");
            Console.WriteLine();
            for (int i = 0; i < defaultPaths.Length; i++)
                Console.WriteLine($"[{i + 1}] {defaultPaths[i].FullName}");
            Console.WriteLine($"[{defaultPaths.Length + 1}] Enter a custom game path.");
            Console.WriteLine();

            string[] validOptions = Enumerable.Range(1, defaultPaths.Length + 1).Select(p => p.ToString(CultureInfo.InvariantCulture)).ToArray();
            string choice = Program.InteractivelyChoose("Type the number next to your choice, then press enter.", validOptions);
            int index = int.Parse(choice, CultureInfo.InvariantCulture) - 1;

            if (index < defaultPaths.Length)
                return defaultPaths[index];
        }
        else
            Console.WriteLine("Oops, couldn't find the game automatically.");

        // let user enter manual path
        while (true)
        {
            // get path from user
            Console.WriteLine();
            Console.WriteLine("Type the file path to the game directory (the one containing 'Stardew Valley.dll'), then press enter.");
            string? path = Console.ReadLine()?.Trim();
            if (string.IsNullOrWhiteSpace(path))
            {
                Console.WriteLine("You must specify a directory path to continue.");
                continue;
            }

            // normalize path
            path = EnvironmentUtility.DetectPlatform() is Platform.Windows
                ? path.Replace("\"", "") // in Windows, quotes are used to escape spaces and aren't part of the file path
                : path.Replace("\\ ", " "); // in Linux/macOS, spaces in paths may be escaped if copied from the command line
            if (path.StartsWith("~/"))
            {
                string home = Environment.GetEnvironmentVariable("HOME") ?? Environment.GetEnvironmentVariable("USERPROFILE")!;
                path = Path.Combine(home, path.Substring(2));
            }

            // get directory
            if (File.Exists(path))
                path = Path.GetDirectoryName(path)!;
            DirectoryInfo gameDir = new(path);
            DirectoryInfo modsDir = new(Path.Combine(path, "Mods"));

            // validate path
            if (!gameDir.Exists)
            {
                Console.WriteLine("That directory doesn't seem to exist.");
                continue;
            }
            if (!gameScanner.LooksLikeGameFolder(gameDir))
            {
                Console.WriteLine("That directory doesn't seem to contain a valid install of Stardew Valley.");
                continue;
            }

            if (!modsDir.Exists)
            {
                Console.WriteLine("That looks like a valid Stardew Valley game folder, but it doesn't have a Mods folder.");
                continue;
            }

            return gameDir;
        }
    }

    /// <summary>Interactively ask the user to choose a value.</summary>
    /// <param name="message">The message to print.</param>
    /// <param name="options">The allowed options (not case sensitive).</param>
    /// <param name="indent">The indentation to prefix to output.</param>
    private static string InteractivelyChoose(string message, string[] options, string indent = "")
    {
        while (true)
        {
            Console.WriteLine(indent + message);
            Console.Write(indent);
            string? input = Console.ReadLine()?.Trim().ToLowerInvariant();
            if (input == null || !options.Contains(input))
            {
                Console.WriteLine($"{indent}That's not a valid option.");
                continue;
            }
            return input;
        }
    }

    /// <summary>Get the mods installed in the given <c>Mods</c> folder.</summary>
    /// <param name="folderPath">The absolute path to the <c>Mods</c> folder to scan.</param>
    private static IEnumerable<ModFolder> GetInstalledMods(string folderPath)
    {
        ModToolkit toolkit = new();
        return toolkit
            .GetModFolders(folderPath, useCaseInsensitiveFilePaths: true)
            .Where(p => p.Type is not (ModType.Ignored or ModType.Invalid));
    }

    /// <summary>Build a directed graph for the given mods.</summary>
    /// <param name="mods">The mods whose dependencies to visualize.</param>
    /// <param name="groupContentPacks">Whether to group mods by content pack, instead of adding a dependency link to their consuming mod.</param>
    private static DirectedGraph BuildDirectedGraph(ModFolder[] mods, bool groupContentPacks)
    {
        DgmlBuilder builder = new()
        {
            NodeBuilders = new[]
            {
                new NodeBuilder<ModFolder>(
                    mod => new Node
                    {
                        Id = mod.Manifest!.UniqueID,
                        Label = mod.DisplayName,
                        Category = mod.Type.ToString()
                    }
                )
            },
            LinkBuilders = new[]
            {
                new LinksBuilder<ModFolder>(mod => GetDependencyLinks(mod, groupContentPacks))
            }
        };

        return builder.Build(mods);
    }

    /// <summary>Export a directed graph to a <c>.dgml</c> file.</summary>
    /// <param name="graph">The directed graph to export.</param>
    /// <param name="filePath">The absolute path to the file to save.</param>
    private static void ExportToDgml(DirectedGraph graph, string filePath)
    {
        using var writer = new StreamWriter(filePath);
        var serializer = new XmlSerializer(graph.GetType());
        serializer.Serialize(writer, graph);
    }

    /// <summary>Generate a PNG image from a given DGML file.</summary>
    /// <param name="filePath">The DGML file path to convert.</param>
    /// <remarks>The generated PNG will be in the same folder as the DGML file. This is experimental and doesn't handle large numbers of mods very well.</remarks>
    private static void ConvertDgmlToPng(string filePath)
    {
        Process.Start(
            new ProcessStartInfo
            {
                FileName = Path.Combine("lib", "DgmlImage", "DgmlImage.exe"),
                ArgumentList = { filePath, /*"/zoom:4", "/width:10000", "/legend"*/ }, // usage docs: http://lovettsoftware.com/posts/dgmlimage
                WorkingDirectory = Environment.CurrentDirectory
            }
        );
    }

    /// <summary>Get the DGML dependency links for a mod.</summary>
    /// <param name="mod">The mod whose dependencies to fetch.</param>
    /// <param name="groupContentPacks">Whether to group mods by content pack, instead of adding a dependency link to their consuming mod.</param>
    private static IEnumerable<Link> GetDependencyLinks(ModFolder mod, bool groupContentPacks)
    {
        // get manifest
        Manifest? manifest = mod.Manifest;
        if (manifest is null)
            yield break;

        // content pack consumer
        if (manifest.ContentPackFor?.UniqueID is not null)
        {
            if (groupContentPacks)
            {
                yield return new Link
                {
                    Source = manifest.ContentPackFor.UniqueID,
                    Target = manifest.UniqueID,
                    IsContainment = true
                };
            }
            else
            {
                yield return new Link
                {
                    Source = manifest.UniqueID,
                    Target = manifest.ContentPackFor.UniqueID
                };
            }
        }

        // required dependencies
        foreach (IManifestDependency dependency in manifest.Dependencies)
        {
            if (!dependency.IsRequired)
                continue;

            yield return new Link
            {
                Source = manifest.UniqueID,
                Target = dependency.UniqueID,
                //Stroke = "Dashed"
            };
        }
    }
}
