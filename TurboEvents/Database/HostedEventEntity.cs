using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace TurboEvents.Database;

/// <summary>
/// One event a host ran: where, what, when it started and ended, and the most players its room
/// held while it ran. Running while <see cref="EndedAt"/> is null; a room runs one at a time. The
/// cooldowns are read from here, so they hold across a restart.
/// </summary>
[Table("hosted")]
[Index(nameof(RoomId), nameof(EndedAt))]
[Index(nameof(HostId), nameof(StartedAt))]
[Index(nameof(StartedAt))]
public class HostedEventEntity
{
    public const string ENDED = "ended";
    public const string EXPIRED = "expired";

    public const int MAX_NAME = 64;
    public const int MAX_TITLE = 128;

    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("room_id")]
    public int RoomId { get; set; }

    [Column("room_name")]
    [MaxLength(MAX_NAME)]
    public string RoomName { get; set; } = string.Empty;

    [Column("host_id")]
    public int HostId { get; set; }

    [Column("host_name")]
    [MaxLength(MAX_NAME)]
    public string HostName { get; set; } = string.Empty;

    [Column("title")]
    [MaxLength(MAX_TITLE)]
    public string Title { get; set; } = string.Empty;

    [Column("started_at")]
    public DateTime StartedAt { get; set; }

    [Column("ended_at")]
    public DateTime? EndedAt { get; set; }

    /// <summary><see cref="ENDED"/> (by <c>:endevent</c>) or <see cref="EXPIRED"/> (it ran past <c>MaxEventMinutes</c>).</summary>
    [Column("end_reason")]
    [MaxLength(16)]
    public string? EndReason { get; set; }

    [Column("ended_by")]
    [MaxLength(MAX_NAME)]
    public string? EndedBy { get; set; }

    /// <summary>How many players online were called to it.</summary>
    [Column("called")]
    public int Called { get; set; }

    /// <summary>The most players the room held, counted every <c>AttendanceSampleSeconds</c> while it ran.</summary>
    [Column("peak_attendees")]
    public int PeakAttendees { get; set; }
}
