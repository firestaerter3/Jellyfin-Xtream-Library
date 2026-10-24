// Only thrown and caught inside GetStringWithRetryAsync, never leaves the class.
#pragma warning disable CA1064

// Copyright (C) 2022  Kevin Jilissen

// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.

// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

using System;

namespace Jellyfin.Xtream.Library.Client;

/// <summary>
/// Raised by <see cref="XtreamClient"/> when the provider answers with an empty JSON list, so the
/// retry loop can treat it like any other transient failure.
/// </summary>
internal class EmptyResponseException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="EmptyResponseException"/> class.
    /// </summary>
    public EmptyResponseException()
        : base("Empty JSON array response.")
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="EmptyResponseException"/> class.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    public EmptyResponseException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="EmptyResponseException"/> class.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    /// <param name="innerException">The exception that is the cause of the current exception.</param>
    public EmptyResponseException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
