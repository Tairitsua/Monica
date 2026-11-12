; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
AC1002 | RequestOwnedWebApi | Error | Handler contains endpoint metadata
AC1003 | RequestOwnedWebApi | Error | Missing Web API generation configuration
AC1004 | RequestOwnedWebApi | Error | Invalid Web API generation configuration
AC1005 | RequestOwnedWebApi | Error | Invalid published request namespace
AC1006 | RequestOwnedWebApi | Error | Invalid endpoint request name
AC1007 | RequestOwnedWebApi | Error | Invalid request result contract
AC1008 | RequestOwnedWebApi | Error | Handler and request result contracts differ
AC1009 | RequestOwnedWebApi | Error | Duplicate endpoint route
AC1010 | RequestOwnedWebApi | Error | Duplicate RPC operation
AC1011 | RequestOwnedWebApi | Error | Invalid endpoint route
AC1012 | RequestOwnedWebApi | Error | Route placeholder has no request property
AC1013 | RequestOwnedWebApi | Error | Published endpoint binding is not supported
AC1014 | RequestOwnedWebApi | Error | Endpoint request has no XML summary
AC1015 | RequestOwnedWebApi | Error | Published endpoint result is not supported
AC1016 | RequestOwnedWebApi | Error | Invalid RPC operation name
AC1017 | RequestOwnedWebApi | Error | Published endpoint result is not a self-constructing remote result envelope
AC1018 | RequestOwnedWebApi | Error | Route property cannot receive the route value
AC1019 | RequestOwnedWebApi | Warning | DELETE endpoint binds a collection or complex request from the query string
AC1020 | RequestOwnedWebApi | Error | Selected handler assembly has no owned operation implementations
AC1021 | RequestOwnedWebApi | Error | Invalid application assembly ownership declaration
AC1022 | RequestOwnedWebApi | Error | Application request has multiple handler implementations
AC1023 | RequestOwnedWebApi | Error | Published application handler has no role owner
AC1024 | RequestOwnedWebApi | Error | Application role refers to an undefined domain
AC1025 | RequestOwnedWebApi | Error | Request execution requirement conflicts with its handler role
AC1026 | RequestOwnedWebApi | Error | Generated application API contains a request without an owned handler
AC1027 | RequestOwnedWebApi | Error | Missing, stale, or inconsistent application ownership catalog metadata
AC1028 | RequestOwnedWebApi | Error | Missing or conflicting Query/Command request intent
AC1099 | RequestOwnedWebApi | Error | Request-owned API generation failed
