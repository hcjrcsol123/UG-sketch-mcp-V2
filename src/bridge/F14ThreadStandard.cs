using System;
using System.Globalization;
using System.IO;
using System.Security;
using System.Xml;

// This is configuration-table evidence, not native feature readback.
public sealed class F14ThreadStandardResult
{
    private readonly string filePath, standard, size, radialEngage, unit, rawPitchText;
    private readonly double rawPitch, pitchMm, tapDrillDiameterMm, majorDiameterMm, minorDiameterMm, angleDeg;
    private readonly int numStarts;
    private readonly bool tapered;

    internal F14ThreadStandardResult(string path, string standardValue, string sizeValue, string engageValue,
        string unitValue, string pitchText, double pitch, double pitchMetric, double tapMetric,
        double majorMetric, double minorMetric, double angle, int starts, bool isTapered)
    {
        filePath = path; standard = standardValue; size = sizeValue; radialEngage = engageValue;
        unit = unitValue; rawPitchText = pitchText; rawPitch = pitch; pitchMm = pitchMetric;
        tapDrillDiameterMm = tapMetric; majorDiameterMm = majorMetric; minorDiameterMm = minorMetric;
        angleDeg = angle; numStarts = starts; tapered = isTapered;
    }

    public string FilePath { get { return filePath; } }
    public string Standard { get { return standard; } }
    public string Size { get { return size; } }
    public string RadialEngage { get { return radialEngage; } }
    public string Unit { get { return unit; } }
    public string RawPitchText { get { return rawPitchText; } }
    public double RawPitch { get { return rawPitch; } }
    public double PitchMm { get { return pitchMm; } }
    public double TapDrillDiameterMm { get { return tapDrillDiameterMm; } }
    public double MajorDiameterMm { get { return majorDiameterMm; } }
    public double MinorDiameterMm { get { return minorDiameterMm; } }
    public double AngleDeg { get { return angleDeg; } }
    public int NumStarts { get { return numStarts; } }
    public bool Tapered { get { return tapered; } }
}

public static class F14ThreadStandard
{
    // The caller must obtain the effective directory from the current NX process.
    public static F14ThreadStandardResult Read(string standardDirectory, string standard, string size,
        string radialEngage, double expectedPitchMm)
    {
        if (String.IsNullOrWhiteSpace(standardDirectory) || String.IsNullOrWhiteSpace(standard) ||
            String.IsNullOrWhiteSpace(size) || String.IsNullOrWhiteSpace(radialEngage) || !PositiveFinite(expectedPitchMm))
            throw new InvalidOperationException("Invalid thread standard lookup arguments.");

        try
        {
            // Rooted drive-relative (C:foo) and current-drive-rooted (\foo) paths
            // still depend on the helper's environment; require a full root.
            if (!Path.IsPathRooted(standardDirectory) || Path.GetPathRoot(standardDirectory).Length < 3)
                throw new InvalidOperationException("Thread standard directory must be absolute.");
            string path = Path.GetFullPath(Path.Combine(standardDirectory, "NX_Thread_Standard.xml"));
            XmlReaderSettings settings = new XmlReaderSettings();
            settings.DtdProcessing = DtdProcessing.Prohibit;
            settings.XmlResolver = null;
            F14ThreadStandardResult match = null;
            using (XmlReader reader = XmlReader.Create(path, settings))
            {
                if (reader.MoveToContent() != XmlNodeType.Element || reader.Name != "Root" || reader.NamespaceURI.Length != 0)
                    throw new InvalidOperationException("Thread standard table has an invalid root.");
                while (reader.Read())
                {
                    if (reader.NodeType != XmlNodeType.Element || reader.Depth != 1 ||
                        reader.Name != "ThreadedHole" || reader.NamespaceURI.Length != 0)
                        continue;
                    if (!String.Equals(reader.GetAttribute("Standard"), standard, StringComparison.Ordinal) ||
                        !String.Equals(reader.GetAttribute("Size"), size, StringComparison.Ordinal) ||
                        !String.Equals(reader.GetAttribute("RadialEngage"), radialEngage, StringComparison.Ordinal))
                        continue;
                    if (match != null)
                        throw new InvalidOperationException("Thread standard lookup is ambiguous.");
                    match = ParseRow(reader, path, standard, size, radialEngage);
                }
            }
            if (match == null)
                throw new InvalidOperationException("Thread standard row was not found.");
            double tolerance = Math.Max(1e-9, 1e-9 * Math.Abs(expectedPitchMm));
            if (Math.Abs(match.PitchMm - expectedPitchMm) > tolerance)
                throw new InvalidOperationException("Thread standard pitch does not match the requested pitch.");
            return match;
        }
        catch (XmlException) { throw new InvalidOperationException("Thread standard table is invalid or unsafe."); }
        catch (IOException) { throw new InvalidOperationException("Thread standard table cannot be read."); }
        catch (UnauthorizedAccessException) { throw new InvalidOperationException("Thread standard table cannot be read."); }
        catch (SecurityException) { throw new InvalidOperationException("Thread standard table cannot be read."); }
        catch (ArgumentException) { throw new InvalidOperationException("Thread standard directory is invalid."); }
        catch (NotSupportedException) { throw new InvalidOperationException("Thread standard directory is invalid."); }
    }

    private static F14ThreadStandardResult ParseRow(XmlReader reader, string path, string standard, string size, string engage)
    {
        string unit = reader.GetAttribute("Unit");
        double factor;
        if (String.Equals(unit, "Metric", StringComparison.Ordinal)) factor = 1.0;
        else if (String.Equals(unit, "Inch", StringComparison.Ordinal)) factor = 25.4;
        else throw new InvalidOperationException("Thread standard row has an unsupported unit.");
        string pitchText = reader.GetAttribute("Pitch");
        double pitch = PositiveNumber(pitchText);
        double tap = PositiveNumber(reader.GetAttribute("TapDrillDia"));
        double major = PositiveNumber(reader.GetAttribute("MajorDiameter"));
        double minor = PositiveNumber(reader.GetAttribute("MinorDiameter"));
        double angle = PositiveNumber(reader.GetAttribute("Angle"));
        int starts;
        if (!Int32.TryParse(reader.GetAttribute("NumStarts"), NumberStyles.Integer, CultureInfo.InvariantCulture, out starts) || starts <= 0)
            throw new InvalidOperationException("Thread standard row has invalid numeric fields.");
        string taperedValue = reader.GetAttribute("Tapered");
        if (taperedValue != "0" && taperedValue != "1")
            throw new InvalidOperationException("Thread standard row has an invalid tapered flag.");
        double pitchMm = pitch * factor, tapMm = tap * factor, majorMm = major * factor, minorMm = minor * factor;
        if (!PositiveFinite(pitchMm) || !PositiveFinite(tapMm) || !PositiveFinite(majorMm) || !PositiveFinite(minorMm))
            throw new InvalidOperationException("Thread standard row has invalid numeric fields.");
        return new F14ThreadStandardResult(path, standard, size, engage, unit, pitchText, pitch,
            pitchMm, tapMm, majorMm, minorMm, angle, starts, taperedValue == "1");
    }

    private static double PositiveNumber(string text)
    {
        double number;
        if (!Double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number) || !PositiveFinite(number))
            throw new InvalidOperationException("Thread standard row has invalid numeric fields.");
        return number;
    }

    private static bool PositiveFinite(double value)
    {
        return value > 0 && !Double.IsNaN(value) && !Double.IsInfinity(value);
    }
}
