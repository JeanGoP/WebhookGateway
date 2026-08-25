namespace WebhookGateway.Core.Common;

/// <summary>
/// Un fallo esperado. No es una excepción: es un resultado posible del flujo.
/// </summary>
/// <remarks>
/// Se llama <c>Failure</c> y no <c>Error</c> porque <c>Error</c> es palabra reservada en
/// otros lenguajes de .NET y complicaría el consumo desde ellos.
/// </remarks>
public readonly record struct Failure(string Code, string Message)
{
    public static readonly Failure None = new(string.Empty, string.Empty);

    public override string ToString() => $"{Code}: {Message}";
}

/// <summary>
/// Resultado sin valor de retorno, y punto de entrada a las fábricas de <see cref="Result{T}"/>.
/// </summary>
public readonly record struct Result
{
    internal Result(bool isSuccess, Failure error)
    {
        IsSuccess = isSuccess;
        Error = error;
    }

    public bool IsSuccess { get; }

    public bool IsFailure => !IsSuccess;

    public Failure Error { get; }

    public static Result Ok() => new(true, Failure.None);

    public static Result Fail(Failure error) => new(false, error);

    public static Result Fail(string code, string message) => new(false, new Failure(code, message));

    /*
        Las fábricas genéricas viven aquí y no dentro de Result<T> por dos razones: los
        miembros estáticos en tipos genéricos obligan a escribir el tipo a mano
        (Result<Pedido>.Ok(x)), y el analizador los desaconseja. Desde aquí el compilador
        infiere T solo: Result.Ok(pedido).
    */

    public static Result<T> Ok<T>(T value) => new(true, value, Failure.None);

    public static Result<T> Fail<T>(Failure error) => new(false, default, error);

    public static Result<T> Fail<T>(string code, string message) => new(false, default, new Failure(code, message));
}

/// <summary>
/// Resultado con valor. <see cref="Value"/> solo es válido si <see cref="IsSuccess"/>.
/// </summary>
public readonly record struct Result<T>
{
    internal Result(bool isSuccess, T? value, Failure error)
    {
        IsSuccess = isSuccess;
        _value = value;
        Error = error;
    }

    private readonly T? _value;

    public bool IsSuccess { get; }

    public bool IsFailure => !IsSuccess;

    public Failure Error { get; }

    /// <summary>Lanza si el resultado es un fallo. Usa <see cref="Match{TOut}"/> para evitarlo.</summary>
    public T Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException($"Se leyó Value de un resultado fallido ({Error}).");

    public TOut Match<TOut>(Func<T, TOut> onSuccess, Func<Failure, TOut> onFailure) =>
        IsSuccess ? onSuccess(_value!) : onFailure(Error);

    public bool TryGetValue(out T value)
    {
        value = _value!;
        return IsSuccess;
    }

    /// <summary>Permite devolver el valor directamente donde se espera un resultado.</summary>
    public static implicit operator Result<T>(T value) => Result.Ok(value);
}
