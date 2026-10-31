// SPDX-License-Identifier: GPL-2.0-or-later

namespace GeoGuessrAccountShadowBot.Domain;

/// <summary>
/// The broad kinds of expected failure, which decide how a failure is shown to a person.
/// </summary>
public enum ErrorKind
{
    Validation,
    Forbidden,
    NotFound,
    Unavailable,
    Unexpected
}

/// <summary>
/// An expected failure, with a stable code in the form <c>{area}.{kind}</c> and a message safe to show.
/// </summary>
public sealed record Error(ErrorKind Kind, string Code, string Message);

/// <summary>
/// The outcome of an operation that can fail in an expected way.
/// </summary>
public readonly record struct Result
{
    private Result(Error? error) => Error = error;

    public Error? Error { get; }

    public bool IsSuccess => Error is null;

    public static Result Success() => new(null);

    public static Result Failure(Error error) => new(error);

    public static implicit operator Result(Error error) => Failure(error);
}
