using System.Buffers.Text;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace OmniRelay.Core.Extensions;

internal sealed class DslProgram
{
    private readonly ImmutableArray<DslInstruction> _instructions;
    internal ExtensionPackage Package { get; }

    private DslProgram(ExtensionPackage package, ImmutableArray<DslInstruction> instructions)
    {
        Package = package;
        _instructions = instructions;
    }

    public static bool TryParse(
        ExtensionPackage package,
        ImmutableHashSet<DslOpcode> allowlist,
        int maxInstructions,
        out DslProgram program,
        out string? error)
    {
        program = default!;
        error = null;

        if (package.Payload is null || package.Payload.Length == 0)
        {
            error = "empty payload";
            return false;
        }

        var utf8 = package.Payload;
        var instructions = ImmutableArray.CreateBuilder<DslInstruction>();

        var text = Encoding.UTF8.GetString(utf8);
        var lines = text.Split('\n');

        foreach (var rawLine in lines)
        {
            if (instructions.Count >= maxInstructions)
            {
                error = "instruction limit exceeded during parse";
                return false;
            }

            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (!TryParseInstruction(line, allowlist, out var instr, out error))
            {
                return false;
            }

            instructions.Add(instr);
        }

        program = new DslProgram(package, instructions.ToImmutable());
        return true;
    }

    public bool Execute(
        ReadOnlySpan<byte> input,
        int maxInstructions,
        int maxOutputBytes,
        TimeSpan maxDuration,
        ValueStopwatch stopwatch,
        out byte[] output,
        out string? watchdogReason)
    {
        watchdogReason = null;
        var buffer = new List<byte>(Math.Max(32, input.Length));
        buffer.AddRange(input.ToArray());
        var instructionCount = 0;

        foreach (var instr in _instructions)
        {
            instructionCount++;
            if (instructionCount > maxInstructions)
            {
                watchdogReason = "instruction_budget";
                output = Array.Empty<byte>();
                return false;
            }

            if (stopwatch.Elapsed > maxDuration)
            {
                watchdogReason = "time_budget";
                output = Array.Empty<byte>();
                return false;
            }

            if (!Apply(instr, buffer))
            {
                watchdogReason = "invalid_execution";
                output = Array.Empty<byte>();
                return false;
            }

            if (buffer.Count > maxOutputBytes)
            {
                watchdogReason = "output_budget";
                output = Array.Empty<byte>();
                return false;
            }

            if (instr.Opcode == DslOpcode.Ret)
            {
                break;
            }
        }

        output = CollectionsMarshal.AsSpan(buffer).ToArray();
        return true;
    }

    private static bool Apply(in DslInstruction instr, List<byte> buffer)
    {
        var span = CollectionsMarshal.AsSpan(buffer);
        switch (instr.Opcode)
        {
            case DslOpcode.Ret:
                return true;
            case DslOpcode.Set:
                buffer.Clear();
                buffer.AddRange(instr.Argument);
                return true;
            case DslOpcode.Append:
                buffer.AddRange(instr.Argument);
                return true;
            case DslOpcode.Upper:
                for (var i = 0; i < span.Length; i++)
                {
                    span[i] = (byte)char.ToUpperInvariant((char)span[i]);
                }
                return true;
            case DslOpcode.Lower:
                for (var i = 0; i < span.Length; i++)
                {
                    span[i] = (byte)char.ToLowerInvariant((char)span[i]);
                }
                return true;
            case DslOpcode.Truncate:
                if (!Utf8Parser.TryParse(instr.Argument, out int len, out _))
                {
                    return false;
                }
                if (len < 0)
                {
                    len = 0;
                }
                if (len > buffer.Count)
                {
                    len = buffer.Count;
                }
                buffer.RemoveRange(len, buffer.Count - len);
                return true;
            default:
                return false;
        }
    }

    private static bool TryParseInstruction(string line, ImmutableHashSet<DslOpcode> allowlist, out DslInstruction instr, out string? error)
    {
        instr = default;
        error = null;

        var firstSpace = line.IndexOf(' ');
        string op;
        string arg;
        if (firstSpace < 0)
        {
            op = line;
            arg = string.Empty;
        }
        else
        {
            op = line[..firstSpace];
            arg = line[(firstSpace + 1)..]; // preserve leading spaces inside argument
        }

        if (!TryParseOpcode(op, out var opcode))
        {
            error = $"unknown opcode '{op}'";
            return false;
        }

        if (!allowlist.Contains(opcode))
        {
            error = $"opcode '{opcode}' not allowed";
            return false;
        }

        instr = new DslInstruction(opcode, Encoding.UTF8.GetBytes(arg));
        return true;
    }

    private static bool TryParseOpcode(string op, out DslOpcode opcode)
    {
        opcode = default;
        if (op.Equals("RET", StringComparison.OrdinalIgnoreCase))
        {
            opcode = DslOpcode.Ret;
            return true;
        }

        if (op.Equals("SET", StringComparison.OrdinalIgnoreCase))
        {
            opcode = DslOpcode.Set;
            return true;
        }

        if (op.Equals("APPEND", StringComparison.OrdinalIgnoreCase))
        {
            opcode = DslOpcode.Append;
            return true;
        }

        if (op.Equals("UPPER", StringComparison.OrdinalIgnoreCase))
        {
            opcode = DslOpcode.Upper;
            return true;
        }

        if (op.Equals("LOWER", StringComparison.OrdinalIgnoreCase))
        {
            opcode = DslOpcode.Lower;
            return true;
        }

        if (op.Equals("TRUNC", StringComparison.OrdinalIgnoreCase))
        {
            opcode = DslOpcode.Truncate;
            return true;
        }

        return false;
    }

    private readonly struct DslInstruction
    {
        public DslOpcode Opcode { get; }
        public byte[] Argument { get; }

        public DslInstruction(DslOpcode opcode, byte[] argument)
        {
            Opcode = opcode;
            Argument = argument;
        }
    }
}

/// <summary>Minimal stopwatch that avoids allocations.</summary>
internal readonly struct ValueStopwatch
{
    private static readonly double TimestampToTicks = TimeSpan.TicksPerSecond / (double)Stopwatch.Frequency;
    private readonly long _startTimestamp;

    private ValueStopwatch(long startTimestamp) => _startTimestamp = startTimestamp;

    public static ValueStopwatch StartNew() => new(Stopwatch.GetTimestamp());

    public TimeSpan Elapsed => new((long)((Stopwatch.GetTimestamp() - _startTimestamp) * TimestampToTicks));
}
