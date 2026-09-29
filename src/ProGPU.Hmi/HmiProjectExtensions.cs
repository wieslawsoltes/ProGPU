namespace ProGPU.Hmi;

internal static class HmiProjectExtensions
{
    internal static void Validate(HmiProject project)
    {
        if (project.Connections is not { Count: <= 64 }) throw new InvalidDataException("A project supports at most 64 connection profiles.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        int mappings = 0;
        foreach (var connection in project.Connections)
        {
            if (connection == null) throw new InvalidDataException("Null connection profile.");
            connection.Validate(project.Tags);
            if (!ids.Add(connection.Id)) throw new InvalidDataException("Duplicate connection ID.");
            mappings += connection.Mappings.Count;
        }
        if (mappings > 16384) throw new InvalidDataException("Project mapping budget exceeded.");
        foreach (var element in project.Screens.SelectMany(s => s.Elements))
        {
            if (!float.IsFinite(element.FaceplateSourceX) || !float.IsFinite(element.FaceplateSourceY) ||
                Math.Abs(element.FaceplateSourceX) > 32768 || Math.Abs(element.FaceplateSourceY) > 32768)
                throw new InvalidDataException("Invalid faceplate master-local coordinates.");
            if (element.States is not { Count: <= 32 }) throw new InvalidDataException("A component supports at most 32 state rules.");
            if (element.FaceplateTemplateId == null || element.FaceplateInstanceId == null || element.FaceplateSourceId == null || element.FaceplatePrefix == null ||
                element.FaceplateTemplateId.Length > 128 || element.FaceplateInstanceId.Length > 128 || element.FaceplateSourceId.Length > 128 || element.FaceplatePrefix.Length > 128 ||
                (element.FaceplateTemplateId.Length == 0) != (element.FaceplateInstanceId.Length == 0)) throw new InvalidDataException("Malformed equipment instance link.");
            foreach (var rule in element.States)
            {
                if (rule == null || !Enum.IsDefined(rule.Condition) || !Enum.IsDefined(rule.Tone) || !double.IsFinite(rule.Threshold) ||
                    rule.Text == null || rule.Text.Length > 128 || rule.Priority is < -10000 or > 10000)
                    throw new InvalidDataException("Invalid equipment state rule.");
                var tag = project.Tags.SingleOrDefault(t => t.Name == rule.Tag);
                var expected = rule.Condition is HmiStateCondition.IsTrue or HmiStateCondition.IsFalse ? HmiTagType.Boolean : HmiTagType.Number;
                if (tag == null || tag.Type != expected) throw new InvalidDataException("Equipment state condition does not match an existing tag type.");
            }
        }
        HmiFaceplates.ValidateLibrary(project);
    }
}
