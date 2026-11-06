# Building Block Context Metadata

Use this reference when authoring or maintaining discovered Building Block metadata. The annotations form the agent-readable architecture catalog shown by Monica's status dashboard and typed APIs.

## Required Decisions

Resolve these facts from the project specification, existing annotations, and actual requirements before writing annotations:

- `$RequirementId$`: a stable requirement identifier owned by the application or its workflow system.
- `$SubdomainTag$`: the bounded context or subdomain tag.
- `$FeatureTag$`: the capability or feature tag.
- `$Title$` and `$Description$`: a concise title and one-sentence responsibility specific to this building block, in the catalog language below.

Use an applicable existing requirement when it can be resolved. Do not invent requirement IDs, tags, or a behavioral commitment to fill the dashboard. Keep the title and description appropriate to the concrete building block, and preserve known associations when maintaining it.

If no legitimate requirement ID or tag convention is available, report the traceability/coverage gap through the project's existing convention and continue an ordinary fix within approved patterns. Do not create a Workflow behavior specification or Change, or seek a new architectural approval, solely for annotation completeness. A project may explicitly require stronger coverage; apply that adopted rule when present. Ask for missing information only when it affects the required behavior or an actual authority decision, rather than blocking maintenance on metadata debt.

Leave `Owner` unset while the application does not use ownership mapping. If the project adopts ownership later, reference a project-defined `Const` entry that maps to a stable team or member ID. Do not use arbitrary service names, team labels, roles, or capabilities as owner values. Ownership is optional and does not block creating a building block.

## Catalog Language

Resolve the metadata language and its configuration source from the project's application specification before writing human-readable metadata. It governs the title and `Description` even though the values are C# strings: `zh-CN` uses Chinese and `en-US` uses English. If no workspace language is configured, follow the application's existing catalog convention. English requirements for conversation, code comments, XML documentation or framework guidance do not override the application catalog language.

Keep `Owner`, tags, requirement IDs, namespaces and CLR type names stable. Localize the human title and responsibility without translating these identities.

## Annotation Pattern

```csharp
using Monica.Application.Abstractions;
using Monica.Core.Results;
using Monica.BuildingBlocks.Annotations;

[BuildingBlockMetadata(
    "$Title$",
    Description = "$Description$",
    Tags = ["$SubdomainTag$", "$FeatureTag$"])]
[BuildingBlockRequirement("$RequirementId$")]
public sealed class CommandHandlerApproveOrder : ApplicationRequestHandler<CommandApproveOrder, Result>
{
    // Implementation omitted.
}
```

Apply the attributes directly to every discovered class or record. Metadata does not flow from a base class. Add another `[BuildingBlockRequirement("...")]` for each independently traceable requirement.

## Normalization And Diagnostics

- Monica trims titles, owners, descriptions, tags, and requirement IDs.
- Duplicate tags and requirement IDs are removed case-insensitively.
- Monica does not validate a project-specific requirement ID pattern.
- Missing annotations are coverage debt, not startup failures.
- Coverage diagnostics describe metadata completeness; discovery and coverage do not approve a role, extension family, or local deviation.
- Explicit but empty titles, owners, tags, or requirement IDs produce catalog warnings.
- Keep titles concise because Serilog source enrichment and management UI use the normalized title.

## Requirement Navigation

Requirement links are host-owned. A host may implement `IBuildingBlockRequirementLinkResolver` and register it with `UseRequirementLinkResolver<TResolver>()`. The resolver runs only when detail is requested; unresolved IDs remain visible and non-clickable.

Do not put paths or URLs into the annotation. Store stable IDs and let the host resolver map them to the current documentation system.
