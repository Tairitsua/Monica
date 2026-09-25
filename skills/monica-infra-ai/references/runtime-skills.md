# Skills available to chat agents

Register `AddAISkillSystem()` when chat agents should discover Monica `Skill<TSelf>` classes or file-based Agent Framework skills. The module brings in `AddAI()` and XML documentation infrastructure. A class skill remains inert until its concrete type is in the host's type-discovery scope and its required modules are loaded. Keep that scope limited to assemblies whose capabilities the host intends to expose; the skill definition's `IsEnabled` and `RequiredModules` can further constrain startup discovery.

For file skills, pass one skill directory containing `SKILL.md` or a package root to `AddFileSkills(path)`; discovery searches up to two levels deep. Scripts in a discovered skill are visible but fail when called without a runner. `AddFileSkillsWithSubprocessRunner(path)` deliberately enables local script execution, so use it only for trusted roots. The built-in read-only file access skill requires an explicit `AddReadOnlyFileAccessRoot(name, path)`; paths may be absolute or relative to the running application directory, and duplicate root names fail configuration.

```csharp
builder.AddMonica(monica =>
{
    monica.AddAISkillSystem()
        .AddFileSkills("agent-skills")
        .AddReadOnlyFileAccessRoot("handbook", "docs/handbook");
});
```

This registration permits discovery and read-only file access; it does not run file-skill scripts. When capabilities appear missing, check the module graph, type-discovery scope or file-root depth, `IsEnabled`, required modules, and runtime capability switches. `AgentCapabilityFacade.GetManagementInfoAsync()` shows registered skill and MCP entries; `SetCatalogEnabledAsync(...)` and `SetEntryEnabledAsync(...)` change runtime exposure. Runtime switches cannot make an undiscovered class or inaccessible file root appear. File-capability enablement state defaults under `monica_data/ai/capabilities_state.json`.

A `Skill<TSelf>` can also declare `McpServerDefinition` so its annotated tools can be exposed through MCP. `AgentCapabilityFacade.SetSkillMcpServerEnabledAsync(...)` persists that choice, but the HTTP endpoint is generally materialized at startup, so changing exposure may need a host restart. See [MCP](mcp.md) for transport and authorization; ordinary in-process skills need no MCP endpoint. The packaged `AddAIUI()` includes the skill system and capability management page, but still needs a chat provider for chat turns.

Source and checks: `Monica.Core/Skills/Skill.cs`, `Monica.AI/Modules/ModuleSkillSystem.cs`, `Monica.AI/Facades/AgentCapabilityFacade.cs`, and `Monica.AI/AgentCapabilities/Services/FileAgentCapabilityStateStore.cs` with `tests/Test.Monica.AI/AgentCapabilities/Services/FileAgentCapabilityStateStoreTests.cs`. Verify Microsoft Agent Framework file-skill behavior against the exact package version when changing discovery or runner assumptions.
