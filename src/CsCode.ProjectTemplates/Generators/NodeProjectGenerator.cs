using System.Text.Json.Nodes;
using CsCode.Core.Projects;
using CsCode.ProjectTemplates.Engine;
using CsCode.ProjectTemplates.Templates;

namespace CsCode.ProjectTemplates.Generators;

/// <summary>
/// A Node.js package, JavaScript or TypeScript: <c>npm init -y</c> (package.json completed with an ES module entry point and scripts), the sources
/// and a test for Node's built-in runner (<c>node --test</c>, nothing to install). TypeScript also installs <c>typescript</c> and
/// <c>@types/node</c> from the npm registry and runs <c>tsc --init</c> with <c>--rootDir src --outDir dist</c> (checked with npm 11.19 and
/// Node 24). F5: js-debug's <c>pwa-node</c>, as CsCode's own Node templates write it (<c>JsDebugAdapterFactory.ProvideInitialAsync</c>):
/// TypeScript builds first with the <c>tsc: build</c> task and maps dist back to src.
/// </summary>
public sealed class NodeProjectGenerator(IToolRunner runner, bool typeScript) : GeneratorBase(runner)
{
    public const string JavaScriptId = "node-js";
    public const string TypeScriptId = "node-ts";
    public const string BuildTask = "tsc: build";

    public override string Id => typeScript ? TypeScriptId : JavaScriptId;
    public override string DisplayName => typeScript ? "TypeScript (Node.js)" : "JavaScript (Node.js)";
    public override string Description => typeScript ? "npm init, tsc --init, node --test" : "npm init, an ES module, node --test";
    public override string? IconKey => "json";
    public override string? LanguageId => typeScript ? "typescript" : "javascript";
    public override int Order => typeScript ? 70 : 75;

    private static string? Npm => OperatingSystem.IsWindows() ? ToolLocator.Find("npm.cmd", "npm") : ToolLocator.Find("npm");
    private static string? Node => ToolLocator.Find("node");

    public override ValueTask<IReadOnlyList<ProjectOption>> GetOptionsAsync(CancellationToken ct) =>
        ValueTask.FromResult<IReadOnlyList<ProjectOption>>([SampleCode("src/greet: a greeting, the program that prints it, and its test")]);

    public override async ValueTask<ProjectToolCheck> CheckToolsAsync(CancellationToken ct)
    {
        var node = await CheckToolAsync(Node, "node", Tools.Node, ct).ConfigureAwait(false);
        if (!node.IsAvailable) return node;
        if (Npm is null) return ProjectToolCheck.Missing("npm was not found", Tools.Node.ForCurrentPlatform);
        return ProjectToolCheck.Available("node " + node.Summary);
    }

    public override string? ValidateName(string name) => ProjectNames.Npm(name);

    public override async Task<ProjectResult> GenerateAsync(ProjectRequest request, IProjectGenerationProgress progress, CancellationToken ct)
    {
        var npm = Require(Npm, "npm", Tools.Node);
        Require(Node, "node", Tools.Node);
        var dir = request.Directory;
        await StepAsync(progress, "Running npm init", new ToolRun(npm, ["init", "-y"], dir), ct).ConfigureAwait(false);

        var files = new ProjectWriter(dir);
        var ext = typeScript ? "ts" : "js";
        var sample = request.Flag(SampleOption, true);
        progress.Step("Writing package.json and the sources");
        var package = JsonNode.Parse(await File.ReadAllTextAsync(files.PathOf("package.json"), ct).ConfigureAwait(false))!.AsObject();
        package["name"] = request.Name;
        package["type"] = "module";
        package["main"] = typeScript ? "dist/index.js" : "src/index.js";
        package["scripts"] = typeScript
            ? new JsonObject { ["build"] = "tsc -p .", ["start"] = "node dist/index.js", ["test"] = "tsc -p . && node --test dist/" }
            : new JsonObject { ["start"] = "node src/index.js", ["test"] = "node --test" };
        await files.WriteJsonAsync("package.json", package, ct).ConfigureAwait(false);
        if (sample)
        {
            await files.WriteAsync($"src/greet.{ext}", typeScript
                ? "export function greet(name: string): string {\n    return `Hello, ${name}!`;\n}\n"
                : "export function greet(name) {\n    return `Hello, ${name}!`;\n}\n", ct).ConfigureAwait(false);
            await files.WriteAsync($"src/index.{ext}", "import { greet } from \"./greet.js\";\n\nconsole.log(greet(\"world\"));\n", ct).ConfigureAwait(false);
            await files.WriteAsync(typeScript ? "src/greet.test.ts" : "test/greet.test.js", $$"""
                import { test } from "node:test";
                import assert from "node:assert/strict";
                import { greet } from "{{(typeScript ? "./greet.js" : "../src/greet.js")}}";

                test("greet greets", () => {
                    assert.equal(greet("world"), "Hello, world!");
                });
                """, ct).ConfigureAwait(false);
        }
        else
        {
            // an ES module (so tsc's isolated modules accept it) whose main() the test can call
            await files.WriteAsync($"src/index.{ext}", $"export function main(){(typeScript ? ": void" : "")} {{}}\n\nmain();\n", ct).ConfigureAwait(false);
            await files.WriteAsync(typeScript ? "src/index.test.ts" : "test/index.test.js", $$"""
                import { test } from "node:test";
                import assert from "node:assert/strict";
                import { main } from "{{(typeScript ? "./index.js" : "../src/index.js")}}";

                test("main runs", () => {
                    assert.doesNotThrow(main);
                });
                """, ct).ConfigureAwait(false);
        }

        if (typeScript)
        {
            // From the npm registry: offline, the project cannot be built, so it fails here and is removed.
            await StepAsync(progress, "Installing typescript and @types/node", new ToolRun(npm, ["install", "--save-dev", "typescript", "@types/node"], dir), ct).ConfigureAwait(false);
            var tsc = files.PathOf(OperatingSystem.IsWindows() ? "node_modules/.bin/tsc.cmd" : "node_modules/.bin/tsc");
            await StepAsync(progress, "Running tsc --init", new ToolRun(tsc, ["--init", "--rootDir", "src", "--outDir", "dist", "--sourceMap", "--types", "node"], dir), ct).ConfigureAwait(false);
        }

        await files.WriteAsync(".gitignore", CommonFiles.GitIgnore(CommonFiles.NodeIgnore), ct).ConfigureAwait(false);
        await files.WriteAsync(".editorconfig", CommonFiles.EditorConfig("[*.{js,ts,mjs,cjs}]\nindent_size = 4"), ct).ConfigureAwait(false);
        await files.WriteAsync("README.md", CommonFiles.Readme(request.Name, typeScript ? "A TypeScript program for Node.js." : "A JavaScript program for Node.js.",
            typeScript ? [("Build", "npm run build"), ("Run", "npm start"), ("Test", "npm test")] : [("Run", "npm start"), ("Test", "npm test")]), ct).ConfigureAwait(false);
        await files.WriteJsonAsync(VsCodeFiles.LaunchPath, Launch(typeScript), ct).ConfigureAwait(false);
        await files.WriteJsonAsync(VsCodeFiles.TasksPath, typeScript
            ? VsCodeFiles.Tasks(
                VsCodeFiles.ProcessTask(BuildTask, "npx", ["tsc", "-p", "."], "$tsc", group: "build"),
                VsCodeFiles.ProcessTask("npm: test", "npm", ["test"], "$tsc", group: "test"))
            : VsCodeFiles.Tasks(VsCodeFiles.ProcessTask("npm: test", "npm", ["test"], null, group: "test")), ct).ConfigureAwait(false);
        return new ProjectResult(dir, $"src/index.{ext}");
    }

    internal static JsonObject Launch(bool typeScript)
    {
        var config = new JsonObject
        {
            ["name"] = typeScript ? "TypeScript: Launch (tsc build)" : "Node.js: Launch Program",
            ["type"] = "pwa-node",
            ["request"] = "launch",
            ["program"] = typeScript ? "${workspaceFolder}/src/index.ts" : "${workspaceFolder}/src/index.js",
        };
        if (typeScript)
        {
            config["preLaunchTask"] = BuildTask;
            config["outFiles"] = new JsonArray("${workspaceFolder}/dist/**/*.js");
            config["sourceMaps"] = true;
        }
        config["skipFiles"] = new JsonArray("<node_internals>/**");
        config["cwd"] = "${workspaceFolder}";
        config["console"] = "internalConsole";
        return VsCodeFiles.Launch(config);
    }
}

/// <summary>A static website: index.html, a stylesheet and a script, no tools. Opened in the browser by hand or a local server task.</summary>
public sealed class StaticSiteGenerator(IToolRunner runner) : GeneratorBase(runner)
{
    public const string GeneratorId = "static-site";

    public override string Id => GeneratorId;
    public override string DisplayName => "Static Website (HTML/CSS)";
    public override string Description => "index.html, styles.css and script.js";
    public override string? IconKey => "globe";
    public override string? LanguageId => "html";
    public override int Order => 80;

    public override ValueTask<IReadOnlyList<ProjectOption>> GetOptionsAsync(CancellationToken ct) =>
        ValueTask.FromResult<IReadOnlyList<ProjectOption>>([SampleCode("A greeting the script writes into the page")]);

    public override ValueTask<ProjectToolCheck> CheckToolsAsync(CancellationToken ct) => ValueTask.FromResult(ProjectToolCheck.Available("no tools needed"));

    public override string? ValidateName(string name) => ProjectNames.Folder(name);

    public override async Task<ProjectResult> GenerateAsync(ProjectRequest request, IProjectGenerationProgress progress, CancellationToken ct)
    {
        var files = new ProjectWriter(request.Directory);
        progress.Step("Writing index.html, styles.css and script.js");
        var title = System.Net.WebUtility.HtmlEncode(request.Name);
        var sample = request.Flag(SampleOption, true);
        await files.WriteAsync("index.html", $$"""
            <!doctype html>
            <html lang="en">
            <head>
                <meta charset="utf-8">
                <meta name="viewport" content="width=device-width, initial-scale=1">
                <title>{{title}}</title>
                <link rel="stylesheet" href="styles.css">
            </head>
            <body>
                <main>
                    <h1>{{title}}</h1>{{(sample ? "\n            <p id=\"greeting\"></p>" : "")}}
                </main>
                <script src="script.js"></script>
            </body>
            </html>
            """, ct).ConfigureAwait(false);
        await files.WriteAsync("styles.css", """
            :root {
                color-scheme: light dark;
                font-family: system-ui, sans-serif;
            }

            main {
                max-width: 40rem;
                margin: 4rem auto;
                padding: 0 1rem;
            }
            """, ct).ConfigureAwait(false);
        await files.WriteAsync("script.js", sample ? "document.getElementById(\"greeting\").textContent = \"Hello, world!\";\n" : "\"use strict\";\n", ct).ConfigureAwait(false);
        await files.WriteAsync(".gitignore", CommonFiles.GitIgnore(null), ct).ConfigureAwait(false);
        await files.WriteAsync(".editorconfig", CommonFiles.EditorConfig("[*.{html,css,js}]\nindent_size = 4"), ct).ConfigureAwait(false);
        var python = OperatingSystem.IsWindows() ? "python" : "python3";
        await files.WriteAsync("README.md", CommonFiles.Readme(request.Name, "A static website.", [("Serve it locally", $"{python} -m http.server 8000")]), ct).ConfigureAwait(false);
        await files.WriteJsonAsync(VsCodeFiles.TasksPath, VsCodeFiles.Tasks(
            VsCodeFiles.ProcessTask("serve (http://localhost:8000)", python, ["-m", "http.server", "8000"], null)), ct).ConfigureAwait(false);
        return new ProjectResult(request.Directory, "index.html");
    }
}
