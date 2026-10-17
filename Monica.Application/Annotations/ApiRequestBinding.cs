namespace Monica.Application.Annotations;

/// <summary>
/// Controls how a request-owned endpoint binds and transports its request model.
/// </summary>
public enum ApiRequestBinding
{
    /// <summary>Chooses query binding for GET and DELETE and body binding otherwise.</summary>
    Auto,

    /// <summary>Binds request properties from the query string.</summary>
    Query,

    /// <summary>Binds the request from a JSON body.</summary>
    Body,

    /// <summary>Binds the request from form data. Published RPC requests cannot use this value.</summary>
    Form
}
