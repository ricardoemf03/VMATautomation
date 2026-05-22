using System;
using System.Globalization;
using System.IO;
using System.Xml.Serialization;

public static class AutoPlanSettingsReader
{
    public static string SettingsPath
    {
        get
        {
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CaCuSibAutoPlan");
            return Path.Combine(folder, "last-settings.xml");
        }
    }
    public static AutoPlanRuntimeSettings Load()
    {
        if (!File.Exists(SettingsPath)) throw new ApplicationException("No se encontró la configuración de AutoPlan. Ejecuta primero la interfaz CaCuSibAutoPlan y presiona Guardar.");
        try
        {
            var root = new XmlRootAttribute("AutoPlanSettings");
            var serializer = new XmlSerializer(typeof(AutoPlanRuntimeSettings), root);
            using (var reader = new StreamReader(SettingsPath))
            {
                var settings = serializer.Deserialize(reader) as AutoPlanRuntimeSettings;
                if (settings == null) throw new ApplicationException("No se pudo leer la configuración de AutoPlan.");
                return settings;
            }
        }
        catch (InvalidOperationException ex)
        {
            string detail = ex.InnerException != null ? ex.InnerException.Message : ex.Message;
            throw new ApplicationException("No se pudo interpretar el XML de configuración de AutoPlan.\n\nArchivo: " + SettingsPath + "\n\nDetalle: " + detail, ex);
        }
    }
    public static double? ParseNullableDose(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        double value;
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) return value;
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value)) return value;
        throw new ApplicationException("Dosis inválida en la interfaz: " + text);
    }
}

[Serializable]
[XmlRoot("AutoPlanSettings")]
public class AutoPlanRuntimeSettings
{
    public string PlanId { get; set; }
    public string Ptv1Id { get; set; }
    public string Ptv2Id { get; set; }
    public string Ptv3Id { get; set; }
    public string Ptv1DoseGyText { get; set; }
    public string Ptv2DoseGyText { get; set; }
    public string Ptv3DoseGyText { get; set; }
    public string MachineId { get; set; }
    public string CourseId { get; set; }
    public string PrescriptionId { get; set; }
    public string PrescriptionName { get; set; }
    public string PrescriptionTargetId { get; set; }
    public bool HasInguinalNodes { get; set; }
    public bool CropPtvOutsideBody { get; set; }
    public bool UseIntermediateDoseDuringOptimization { get; set; }
    public string VmatMlcId { get; set; }
    public string OptimizationScriptDllName { get; set; }
    public string PlanParametersScriptDllName { get; set; }
    public string DvhEstimationScriptDllName { get; set; }
    public string CalculationScriptDllName { get; set; }
    public DateTime SavedAt { get; set; }
}
