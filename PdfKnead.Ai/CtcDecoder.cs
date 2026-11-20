namespace PdfKnead.Ai;

/// <summary>A character read by the recognition network, with the time step (column of the line image) where it was read.</summary>
/// <param name="Text">The character (one or more UTF-16 units).</param>
/// <param name="Step">The time step.</param>
/// <param name="Probability">How sure the network is.</param>
internal readonly record struct CtcCharacter(string Text, int Step, float Probability);

/// <summary>
/// Reads the output of the recognition network (CTC, "connectionist temporal classification"): per time step the most likely class; repeats
/// of a class are one character, class 0 (the blank) separates them and stands for nothing. The classes after the blank are the characters
/// of the model's description, followed by the space if the network has one class more.
/// </summary>
internal sealed class CtcDecoder
{
    private readonly string[] _classes;

    /// <summary>Prepares the decoder.</summary>
    /// <param name="characters">The characters of the description.</param>
    /// <param name="classes">The number of classes of the network's output.</param>
    /// <exception cref="InvalidDataException">The output does not fit the characters.</exception>
    public CtcDecoder(IReadOnlyList<string> characters, int classes)
    {
        if (classes != characters.Count + 1 && classes != characters.Count + 2)
            throw new InvalidDataException($"The recognition model has {classes} classes for {characters.Count} characters.");
        _classes = new string[classes];
        _classes[0] = "";
        for (var i = 0; i < characters.Count; i++) _classes[i + 1] = characters[i];
        if (classes == characters.Count + 2) _classes[^1] = " ";
    }

    /// <summary>The number of classes.</summary>
    public int Classes => _classes.Length;

    /// <summary>Decodes one line.</summary>
    /// <param name="output">The probabilities, time step by time step, <see cref="Classes"/> per step.</param>
    /// <param name="steps">The number of time steps that belong to the line (the rest is padding).</param>
    /// <returns>The characters in order.</returns>
    public List<CtcCharacter> Decode(ReadOnlySpan<float> output, int steps)
    {
        var result = new List<CtcCharacter>();
        var previous = -1;
        steps = Math.Min(steps, output.Length / _classes.Length);
        for (var t = 0; t < steps; t++)
        {
            var row = output.Slice(t * _classes.Length, _classes.Length);
            var best = 0;
            for (var c = 1; c < row.Length; c++)
                if (row[c] > row[best]) best = c;
            if (best != 0 && best != previous) result.Add(new CtcCharacter(_classes[best], t, row[best]));
            previous = best;
        }
        return result;
    }
}
