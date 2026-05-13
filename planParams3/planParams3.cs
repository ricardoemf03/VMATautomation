using System;
using System.Linq;
using System.Text;
using System.Windows;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using VMS.TPS.Common.Model.API;
using VMS.TPS.Common.Model.Types;

// TODO: Replace the following version attributes by creating AssemblyInfo.cs. You can do this in the properties of the Visual Studio project.
[assembly: AssemblyVersion("1.0.0.1")]
[assembly: AssemblyFileVersion("1.0.0.1")]
[assembly: AssemblyInformationalVersion("1.0")]








[assembly: ESAPIScript(IsWriteable = true)]

namespace VMS.TPS
{
    public class Script
    {
        public Script()
        {
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Execute(ScriptContext context /*, System.Windows.Window window, ScriptEnvironment environment */)
        {
            ValidateContext(context);

            if (!context.Patient.CanModifyData())
            {
                throw new ApplicationException("El sistema no permite modificar datos con este script en el estado actual.");
            }

            context.Patient.BeginModifications();

            var options = BuildOptions();
            var structureSet = context.StructureSet;

            var selectedCourse = GetRequiredCourse(context.Patient, options.SelectedCourseId);
            var selectedTargetStructure = GetRequiredStructure(structureSet, options.SelectedStructureTargetId);

            var selectedPrescription = GetRequiredPrescriptionSelection(
                selectedCourse,
                structureSet,
                options.SelectedPrescriptionName,
                options.SelectedPrescriptionTargetId,
                options.SelectedStructureTargetId,
                options.DefaultTreatmentPercentageDecimal);

            var plan = GetOrCreateEmptyPlan(selectedCourse, structureSet, options.NewPlanId);

            ApplyPlanTarget(plan, selectedTargetStructure);
            ApplyPlanPrescription(plan, selectedPrescription);
            //ApplyPhotonDoseCalculationModel(plan, options);
            

            var treatmentMachine = BuildTreatmentMachineParameters(options);
            var isoConfiguration = ResolveIsocenterConfiguration(structureSet, plan, selectedTargetStructure, options);

            CreateTreatmentBeams(plan, treatmentMachine, isoConfiguration, options);
            CreateImagingSetup(plan, treatmentMachine, selectedTargetStructure, options);

            ApplyCalculationModels(plan, options);

            ShowCompletionSummary(selectedCourse, plan, selectedPrescription, isoConfiguration, options);
        }


        // =============================================================
        // HELPERS
        // =============================================================

        private static PlanCreationOptions BuildOptions()
        {
            string machineId = "HAL1102";
            return new PlanCreationOptions
            {
                // Estos valores quedan como placeholders para conectarlos luego a la GUI (ComboBox / CheckBox).
                SelectedCourseId = "C1",
                SelectedPrescriptionName = "BoostSIB",
                SelectedStructureTargetId = "PTV_45Gy",   // Id real de la estructura
                SelectedPrescriptionTargetId = "PTV_45Gy", // TargetId dentro de la Rx
                NewPlanId = "plan1",
                HasInguinalNodes = false,


                MachineId = machineId,
                EnergyModeId = "6X",
                DoseRate = 740,
                TechniqueId = "ARC",
                PrimaryFluenceModeId = "FFF",
                ImagingTechnique = GetDefaultImagingTechniqueForMachine(machineId),

                PhotonVolumeDoseCalculationModelId = "AAA_1801",
                DvhEstimationCalculationModelId = "DVH Estimation Algorithm [18.0.1]",
                PhotonVmatOptimizationModelId = "PO_1801",
                UseGpuForOptimization = false,

                MaxFieldLengthAtIsocenterMm = 280.0,
                MaxTreatmentIsocenterSeparationMm = 105.0,
                SuperiorTreatmentIsocenterLimitInUserMm = 130.0,
                DefaultTreatmentPercentageDecimal = 1.0,
                NumberOfControlPoints = 180,
                CouchAngle = 0.0,
            };
        }

        private static void ValidateContext(ScriptContext context)
        {
            if (context == null)
            {
                throw new ApplicationException("El ScriptContext es null.");
            }

            if (context.Patient == null)
            {
                throw new ApplicationException("No hay paciente cargado.");
            }

            if (context.StructureSet == null)
            {
                throw new ApplicationException("No hay StructureSet activo.");
            }
        }

        private static Course GetRequiredCourse(Patient patient, string selectedCourseId)
        {
            var course = patient.Courses.FirstOrDefault(c => IdEquals(c.Id, selectedCourseId));
            if (course == null)
            {
                throw new ApplicationException(
                    $"No se encontró el curso '{selectedCourseId}'. Selecciona un curso existente desde la interfaz.");
            }

            return course;
        }

        private static Structure GetRequiredStructure(StructureSet structureSet, string structureId)
        {
            var structure = structureSet.Structures.FirstOrDefault(s => IdEquals(s.Id, structureId));
            if (structure == null)
            {
                throw new ApplicationException($"No se encontró la estructura '{structureId}'.");
            }

            if (structure.IsEmpty)
            {
                throw new ApplicationException($"La estructura '{structureId}' existe pero está vacía.");
            }

            return structure;
        }

        private static PrescriptionSelection GetRequiredPrescriptionSelection(
            Course course,
            StructureSet structureSet,
            string selectedPrescriptionName,
            string selectedPrescriptionTargetId,
            string selectedStructureTargetId,
            double defaultTreatmentPercentageDecimal)
        {
            var prescriptions = course.TreatmentPhases
                .Where(tp => tp != null)
                .SelectMany(tp => tp.Prescriptions ?? Enumerable.Empty<RTPrescription>())
                .Where(p => p != null)
                .ToList();

            if (!prescriptions.Any())
            {
                throw new ApplicationException(
                    $"El curso '{course.Id}' no contiene prescripciones en TreatmentPhases.");
            }

            var matchingPrescriptions = prescriptions
                .Where(p => IdEquals(p.Name, selectedPrescriptionName) || IdEquals(p.Id, selectedPrescriptionName))
                .GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            if (matchingPrescriptions.Count == 0)
            {
                var availablePrescriptionNames = string.Join(", ", prescriptions.Select(p => p.Name).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct());

                throw new ApplicationException(
                    $"No se encontró la prescripción '{selectedPrescriptionName}' dentro del curso '{course.Id}'. " +
                    $"Prescripciones disponibles: {availablePrescriptionNames}");
            }

            if (matchingPrescriptions.Count > 1)
            {
                throw new ApplicationException(
                    $"Se encontraron varias prescripciones que coinciden con '{selectedPrescriptionName}' en el curso '{course.Id}'. " +
                    "Haz la selección más específica desde la interfaz.");
            }

            var prescription = matchingPrescriptions.Single();

            var prescriptionTarget = prescription.Targets
                 .FirstOrDefault(t => t != null && IdEquals(t.TargetId, selectedPrescriptionTargetId));

            if (prescriptionTarget == null)
            {
                var availableTargetIds = string.Join(", ", prescription.Targets.Select(t => t.TargetId));

                throw new ApplicationException(
                    $"La prescripción '{prescription.Name}' no contiene el target '{selectedPrescriptionTargetId}'. " +
                    $"Targets disponibles: {availableTargetIds}");
            }

            var targetStructure = GetRequiredStructure(structureSet, selectedStructureTargetId);
            var treatmentPercentage = ResolveTreatmentPercentage(prescriptionTarget, defaultTreatmentPercentageDecimal);

            return new PrescriptionSelection
            {
                Prescription = prescription,
                PrescriptionTarget = prescriptionTarget,
                TargetStructure = targetStructure,
                NumberOfFractions = prescriptionTarget.NumberOfFractions,
                DosePerFraction = prescriptionTarget.DosePerFraction,
                TreatmentPercentageDecimal = treatmentPercentage,
            };
        }

        private static double ResolveTreatmentPercentage(
            RTPrescriptionTarget prescriptionTarget,
            double defaultTreatmentPercentageDecimal)
        {
            // Para objetivos tipo IsodoseLine, ESAPI expone el valor porcentual en Value.
            if (prescriptionTarget.Type == RTPrescriptionTargetType.IsodoseLine && prescriptionTarget.Value > 0.0)
            {
                return prescriptionTarget.Value / 100.0;
            }

            return defaultTreatmentPercentageDecimal;
        }

        private static ExternalPlanSetup GetOrCreateEmptyPlan(Course course, StructureSet structureSet, string newPlanId)
        {
            var existingPlan = course.ExternalPlanSetups.FirstOrDefault(p => IdEquals(p.Id, newPlanId));
            if (existingPlan == null)
            {
                var createdPlan = course.AddExternalPlanSetup(structureSet);
                createdPlan.Id = newPlanId;
                return createdPlan;
            }

            if (existingPlan.Beams != null && existingPlan.Beams.Any())
            {
                throw new ApplicationException(
                    $"El plan '{newPlanId}' ya existe y ya contiene campos. Usa otro Id de plan o limpia el plan antes de ejecutar el script.");
            }

            return existingPlan;
        }

        private static void ApplyPlanTarget(ExternalPlanSetup plan, Structure targetStructure)
        {
            var errorHint = new StringBuilder();
            var ok = plan.SetTargetStructureIfNoDose(targetStructure, errorHint);
            if (!ok)
            {
                var details = errorHint.Length > 0 ? errorHint.ToString() : "No se pudo asignar el target al plan.";
                throw new ApplicationException(details);
            }
        }

        private static void ApplyPlanPrescription(ExternalPlanSetup plan, PrescriptionSelection selection)
        {
            plan.SetPrescription(
                selection.NumberOfFractions,
                selection.DosePerFraction,
                selection.TreatmentPercentageDecimal);
        }

        //aqui
        private static void ApplyCalculationModels(ExternalPlanSetup plan, PlanCreationOptions options)
        {
            if (plan == null)
                throw new ApplicationException("El plan es null al intentar asignar modelos de cálculo.");

            if (options == null)
                throw new ApplicationException("Las opciones son null al intentar asignar modelos de cálculo.");

            SetCalculationModelIfAvailable(
                plan,
                CalculationType.PhotonVolumeDose,
                options.PhotonVolumeDoseCalculationModelId,
                "Volume Dose");

            SetCalculationModelIfAvailable(
                plan,
                CalculationType.DVHEstimation,
                options.DvhEstimationCalculationModelId,
                "DVH Estimation");

            SetCalculationModelIfAvailable(
                plan,
                CalculationType.PhotonVMATOptimization,
                options.PhotonVmatOptimizationModelId,
                "Optimization");
            // Sin usar GPU por el momento
            //ApplyOptimizationGpuOptionIfAvailable(plan, options);
        }

        private static void SetCalculationModelIfAvailable(
            ExternalPlanSetup plan,
            CalculationType calculationType,
            string modelId,
            string displayName)
        {
            modelId = (modelId ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(modelId))
                throw new ApplicationException("No se definió el modelo de cálculo para: " + displayName);

            var availableModels = plan
                .GetModelsForCalculationType(calculationType)
                .ToList();

            if (!availableModels.Any(m => IdEquals(m, modelId)))
            {
                string availableText = availableModels.Any()
                    ? string.Join(", ", availableModels)
                    : "No hay modelos disponibles.";

                throw new ApplicationException(
                    "El modelo '" + modelId + "' no está disponible para " + displayName + ".\n\n" +
                    "Modelos disponibles:\n" + availableText);
            }

            plan.SetCalculationModel(calculationType, modelId);

            string selectedModel = plan.GetCalculationModel(calculationType);

            if (!IdEquals(selectedModel, modelId))
            {
                throw new ApplicationException(
                    "No se pudo confirmar el modelo de cálculo para " + displayName + ".\n" +
                    "Esperado: " + modelId + "\n" +
                    "Asignado: " + (selectedModel ?? "null"));
            }
        }

        private static void ApplyOptimizationGpuOptionIfAvailable(
            ExternalPlanSetup plan,
            PlanCreationOptions options)
        {
            string optimizationModelId = (options.PhotonVmatOptimizationModelId ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(optimizationModelId))
                return;

            var calculationOptions = plan.GetCalculationOptions(optimizationModelId);

            if (calculationOptions == null || calculationOptions.Count == 0)
                return;

            var gpuOption = calculationOptions
                .FirstOrDefault(kvp =>
                    kvp.Key != null &&
                    kvp.Key.IndexOf("GPU", StringComparison.OrdinalIgnoreCase) >= 0);

            if (string.IsNullOrWhiteSpace(gpuOption.Key))
            {
                // No lanzo error porque puede ser que el modelo no exponga esa opción por ESAPI.
                MessageBox.Show(
                    "No se encontró una opción de GPU expuesta por ESAPI para el modelo '" +
                    optimizationModelId + "'.\n\n" +
                    "Se asignó el modelo de optimización, pero no se modificó el uso de GPU.",
                    "Use GPU",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                return;
            }

            string desiredValue = ConvertBoolToCalculationOptionValue(
                options.UseGpuForOptimization,
                gpuOption.Value);

            bool ok = plan.SetCalculationOption(
                optimizationModelId,
                gpuOption.Key,
                desiredValue);

            if (!ok)
            {
                throw new ApplicationException(
                    "No se pudo asignar la opción GPU del modelo '" + optimizationModelId + "'.\n" +
                    "Opción encontrada: " + gpuOption.Key + "\n" +
                    "Valor intentado: " + desiredValue);
            }
        }

        private static string ConvertBoolToCalculationOptionValue(bool value, string currentValue)
        {
            string current = (currentValue ?? string.Empty).Trim();

            if (string.Equals(current, "true", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(current, "false", StringComparison.OrdinalIgnoreCase))
            {
                return value ? "true" : "false";
            }

            if (string.Equals(current, "ON", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(current, "OFF", StringComparison.OrdinalIgnoreCase))
            {
                return value ? "ON" : "OFF";
            }

            if (string.Equals(current, "Yes", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(current, "No", StringComparison.OrdinalIgnoreCase))
            {
                return value ? "Yes" : "No";
            }

            if (current == "1" || current == "0")
            {
                return value ? "1" : "0";
            }

            // Valor por defecto si ESAPI devuelve algo no esperado.
            return value ? "true" : "false";
        }

        private static ExternalBeamMachineParameters BuildTreatmentMachineParameters(PlanCreationOptions options)
        {
            return new ExternalBeamMachineParameters(
                options.MachineId,
                options.EnergyModeId,
                options.DoseRate,
                options.TechniqueId,
                options.PrimaryFluenceModeId);
        }

        private static IsocenterConfiguration ResolveIsocenterConfiguration(
            StructureSet structureSet,
            ExternalPlanSetup plan,
            Structure targetStructure,
            PlanCreationOptions options)
        {
            var image = structureSet.Image;
            var mesh = targetStructure.MeshGeometry;
            if (mesh == null)
            {
                throw new ApplicationException(
                    $"La estructura '{targetStructure.Id}' no tiene MeshGeometry disponible.");
            }

            var bounds = mesh.Bounds;

            // Se mantiene la misma lógica : el eje longitudinal del target se toma de Bounds.Z,
            // y luego se convierte/redondea en coordenadas USER usando DicomToUser/UserToDicom.
            double longitudinalMinDicomMm = bounds.Z;
            double longitudinalMaxDicomMm = bounds.Z + bounds.SizeZ;
            double targetLengthMm = longitudinalMaxDicomMm - longitudinalMinDicomMm;
            double longitudinalCenterDicomMm = (longitudinalMinDicomMm + longitudinalMaxDicomMm) / 2.0;
            //double requiredSeparationMm = Math.Max(0.0, targetLengthMm - options.MaxFieldLengthAtIsocenterMm);
            double superiorMarginMm = 20.0;   // 2 cm
            double inferiorMarginMm = 20.0;   // 2 cm de margen entre el ptv

            double requiredSeparationMm = Math.Max(
                0.0,
                (targetLengthMm + superiorMarginMm + inferiorMarginMm) - options.MaxFieldLengthAtIsocenterMm
            );

            if (requiredSeparationMm > options.MaxTreatmentIsocenterSeparationMm)
            {
                throw new ApplicationException(
                    $"No se puede cubrir el target '{targetStructure.Id}' con dos isocentros Halcyon. " +
                    $"Longitud objetivo = {targetLengthMm:F1} mm, separación requerida = {requiredSeparationMm:F1} mm, " +
                    $"máximo permitido = {options.MaxTreatmentIsocenterSeparationMm:F1} mm.");
            }

            var centerPoint = targetStructure.CenterPoint;

            if (requiredSeparationMm <= 0.0)
            {
                var singleRawDicom = new VVector(centerPoint.x, centerPoint.y, longitudinalCenterDicomMm);
                var singleRounded = RoundSingleIsocenter(singleRawDicom, image, plan);

                return IsocenterConfiguration.CreateSingle(singleRounded, targetLengthMm, requiredSeparationMm);
            }

            var rawDicomA = new VVector(centerPoint.x, centerPoint.y, longitudinalCenterDicomMm - requiredSeparationMm / 2.0);
            var rawDicomB = new VVector(centerPoint.x, centerPoint.y, longitudinalCenterDicomMm + requiredSeparationMm / 2.0);

            var rawUserA = image.DicomToUser(rawDicomA, plan);
            var rawUserB = image.DicomToUser(rawDicomB, plan);

            // Ordenamos primero en USER para que el redondeo inferior/superior no dependa del signo del eje.
            var lowerRawUser = rawUserA.y <= rawUserB.y ? rawUserA : rawUserB;
            var upperRawUser = rawUserA.y <= rawUserB.y ? rawUserB : rawUserA;

            double lowerRoundedY = FloorToCm(lowerRawUser.y);
            double upperRoundedY = CeilToCm(upperRawUser.y);

            if (upperRoundedY - lowerRoundedY < 10.0)
            {
                upperRoundedY = lowerRoundedY + 10.0;
            }

            if (upperRoundedY - lowerRoundedY > options.MaxTreatmentIsocenterSeparationMm)
            {
                upperRoundedY = lowerRoundedY + 100.0;
            }

            var inferiorUser = new VVector(
                RoundToCm(lowerRawUser.x),
                lowerRoundedY,
                RoundToCm(lowerRawUser.z));

            var superiorUser = new VVector(
                RoundToCm(upperRawUser.x),
                upperRoundedY,
                RoundToCm(upperRawUser.z));

            var inferior = new IsoPoint("Inferior", image.UserToDicom(inferiorUser, plan), inferiorUser);
            var superior = new IsoPoint("Superior", image.UserToDicom(superiorUser, plan), superiorUser);

            if (Math.Abs(superior.User.y) > options.SuperiorTreatmentIsocenterLimitInUserMm)
            {
                var message =
                    $"Mover el isocentro porque excede los 13 cm. " +
                    $"Isocentro superior (USER y) = {superior.User.y:F1} mm.";

                MessageBox.Show(message, "Límite de isocentro", MessageBoxButton.OK, MessageBoxImage.Warning);
                throw new ApplicationException(message);
            }

            return IsocenterConfiguration.CreateDual(superior, inferior, targetLengthMm, requiredSeparationMm);
        }

        private static IsoPoint RoundSingleIsocenter(VVector rawDicom, Image image, PlanSetup plan)
        {
            var rawUser = image.DicomToUser(rawDicom, plan);
            var roundedUser = new VVector(
                RoundToCm(rawUser.x),
                RoundToCm(rawUser.y),
                RoundToCm(rawUser.z));

            var roundedDicom = image.UserToDicom(roundedUser, plan);
            return new IsoPoint("Único", roundedDicom, roundedUser);
        }

        private static void CreateTreatmentBeams(
            ExternalPlanSetup plan,
            ExternalBeamMachineParameters machineParameters,
            IsocenterConfiguration isocenters,
            PlanCreationOptions options)
        {
            var metersetWeights = CreateMetersetWeights(options.NumberOfControlPoints);
            double inferiorLastCollimator = options.HasInguinalNodes ? 90.0 : 70.0;

            if (!isocenters.UseDualIsocenter)
            {
                AddVmatBeam(plan, machineParameters, metersetWeights, "C1", 340.0, 179.0, 181.0, GantryDirection.CounterClockwise, options.CouchAngle, isocenters.Single.Dicom);
                AddVmatBeam(plan, machineParameters, metersetWeights, "C2", 20.0, 181.0, 179.0, GantryDirection.Clockwise, options.CouchAngle, isocenters.Single.Dicom);
                AddVmatBeam(plan, machineParameters, metersetWeights, "C3", inferiorLastCollimator, 179.0, 181.0, GantryDirection.CounterClockwise, options.CouchAngle, isocenters.Single.Dicom);
                return;
            }

            AddVmatBeam(plan, machineParameters, metersetWeights, "C1", 340.0, 179.0, 181.0, GantryDirection.CounterClockwise, options.CouchAngle, isocenters.Superior.Dicom);
            AddVmatBeam(plan, machineParameters, metersetWeights, "C2", 20.0, 181.0, 179.0, GantryDirection.Clockwise, options.CouchAngle, isocenters.Superior.Dicom);

            AddVmatBeam(plan, machineParameters, metersetWeights, "C3", 340.0, 179.0, 181.0, GantryDirection.CounterClockwise, options.CouchAngle, isocenters.Inferior.Dicom);
            AddVmatBeam(plan, machineParameters, metersetWeights, "C4", 20.0, 181.0, 179.0, GantryDirection.Clockwise, options.CouchAngle, isocenters.Inferior.Dicom);
            AddVmatBeam(plan, machineParameters, metersetWeights, "C5", inferiorLastCollimator, 179.0, 181.0, GantryDirection.CounterClockwise, options.CouchAngle, isocenters.Inferior.Dicom);
        }

        private static Beam AddVmatBeam(
            ExternalPlanSetup plan,
            ExternalBeamMachineParameters machineParameters,
            IEnumerable<double> metersetWeights,
            string beamId,
            double collimatorAngle,
            double gantryStartAngle,
            double gantryStopAngle,
            GantryDirection gantryDirection,
            double couchAngle,
            VVector isocenter)
        {
            var beam = plan.AddVMATBeamForFixedJaws(
                machineParameters,
                metersetWeights,
                collimatorAngle,
                gantryStartAngle,
                gantryStopAngle,
                gantryDirection,
                couchAngle,
                isocenter);

            beam.Id = beamId;
            return beam;
        }

        private static List<double> CreateMetersetWeights(int numberOfControlPoints)
        {
            if (numberOfControlPoints < 2)
            {
                throw new ApplicationException("El número de control points debe ser al menos 2.");
            }

            return Enumerable.Range(0, numberOfControlPoints)
                .Select(i => (double)i / (numberOfControlPoints - 1))
                .ToList();
        }

        private static void CreateImagingSetup(
            ExternalPlanSetup plan,
            ExternalBeamMachineParameters machineParameters,
            Structure targetStructure,
            PlanCreationOptions options)
        {
            var imagingParameters = new ImagingBeamSetupParameters(
                options.ImagingTechnique,
                0.0,
                0.0,
                0.0,
                0.0,
                options.MaxFieldLengthAtIsocenterMm,
                options.MaxFieldLengthAtIsocenterMm);

            bool ok = plan.AddImagingSetup(machineParameters, imagingParameters, targetStructure);
            if (!ok)
            {
                throw new ApplicationException("AddImagingSetup devolvió false. No se pudieron crear los campos de imaging.");
            }
        }
        //MachineId
        private static ImagingSetup GetDefaultImagingTechniqueForMachine(string machineId)
        {
            if (string.Equals(machineId, "HAL1102", StringComparison.OrdinalIgnoreCase))
                return ImagingSetup.MVCBCT_High_Quality;

            if (string.Equals(machineId, "HAL1753", StringComparison.OrdinalIgnoreCase))
                return ImagingSetup.kVCBCT;

            throw new ApplicationException(
                "No se ha definido una técnica de imagen para la máquina: " + machineId);
        }

        private static void ShowCompletionSummary(
            Course course,
            ExternalPlanSetup plan,
            PrescriptionSelection prescription,
            IsocenterConfiguration isocenters,
            PlanCreationOptions options)
        {
            var info = new StringBuilder();
            info.AppendLine("PlanParameters finalizado correctamente.");
            info.AppendLine();
            info.AppendLine($"Curso: {course.Id}");
            info.AppendLine($"Plan: {plan.Id}");
            info.AppendLine($"Prescripción: {prescription.Prescription.Name}");
            info.AppendLine($"Target seleccionado: {prescription.TargetStructure.Id}");
            info.AppendLine($"Fracciones: {prescription.NumberOfFractions}");
            info.AppendLine($"Dose/Fx: {prescription.DosePerFraction}");
            info.AppendLine($"Treatment %: {prescription.TreatmentPercentageDecimal:P0}");
            info.AppendLine($"Inguinales: {(options.HasInguinalNodes ? "Sí" : "No")}");
            info.AppendLine($"Modo: {(isocenters.UseDualIsocenter ? "Doble isocentro" : "Isocentro único")}");

            if (isocenters.UseDualIsocenter)
            {
                info.AppendLine($"Iso superior USER: ({isocenters.Superior.User.x:F1}, {isocenters.Superior.User.y:F1}, {isocenters.Superior.User.z:F1}) mm");
                info.AppendLine($"Iso inferior USER: ({isocenters.Inferior.User.x:F1}, {isocenters.Inferior.User.y:F1}, {isocenters.Inferior.User.z:F1}) mm");
            }
            else
            {
                info.AppendLine($"Iso único USER: ({isocenters.Single.User.x:F1}, {isocenters.Single.User.y:F1}, {isocenters.Single.User.z:F1}) mm");
            }

            MessageBox.Show(info.ToString(), "PlanParameters", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private static bool IdEquals(string left, string right)
        {
            return string.Equals((left ?? string.Empty).Trim(), (right ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase);
        }

        private static double RoundToCm(double mm)
        {
            return Math.Round(mm / 10.0, MidpointRounding.AwayFromZero) * 10.0;
        }

        private static double FloorToCm(double mm)
        {
            return Math.Floor(mm / 10.0) * 10.0;
        }

        private static double CeilToCm(double mm)
        {
            return Math.Ceiling(mm / 10.0) * 10.0;
        }

        private sealed class PlanCreationOptions
        {
            public string SelectedCourseId { get; set; }
            public string SelectedPrescriptionName { get; set; }
            public string SelectedStructureTargetId { get; set; }
            public string SelectedPrescriptionTargetId { get; set; }
            public string NewPlanId { get; set; }
            public bool HasInguinalNodes { get; set; }

            public string MachineId { get; set; }
            public string EnergyModeId { get; set; }
            public int DoseRate { get; set; }
            public string TechniqueId { get; set; }
            public string PrimaryFluenceModeId { get; set; }
            public ImagingSetup ImagingTechnique { get; set; }

            //public string PhotonDoseCalculationModelId { get; set; }
            public string PhotonVolumeDoseCalculationModelId { get; set; }
            public string DvhEstimationCalculationModelId { get; set; }
            public string PhotonVmatOptimizationModelId { get; set; }
            public bool UseGpuForOptimization { get; set; }

            public double MaxFieldLengthAtIsocenterMm { get; set; }
            public double MaxTreatmentIsocenterSeparationMm { get; set; }
            public double SuperiorTreatmentIsocenterLimitInUserMm { get; set; }
            public double DefaultTreatmentPercentageDecimal { get; set; }
            public int NumberOfControlPoints { get; set; }
            public double CouchAngle { get; set; }
        }

        private sealed class PrescriptionSelection
        {
            public RTPrescription Prescription { get; set; }
            public RTPrescriptionTarget PrescriptionTarget { get; set; }
            public Structure TargetStructure { get; set; }
            public int NumberOfFractions { get; set; }
            public DoseValue DosePerFraction { get; set; }
            public double TreatmentPercentageDecimal { get; set; }
        }

        private sealed class IsoPoint
        {
            public IsoPoint(string label, VVector dicom, VVector user)
            {
                Label = label;
                Dicom = dicom;
                User = user;
            }

            public string Label { get; private set; }
            public VVector Dicom { get; private set; }
            public VVector User { get; private set; }
        }

        private sealed class IsocenterConfiguration
        {
            private IsocenterConfiguration()
            {
            }

            public bool UseDualIsocenter { get; private set; }
            public IsoPoint Single { get; private set; }
            public IsoPoint Superior { get; private set; }
            public IsoPoint Inferior { get; private set; }
            public double TargetLengthMm { get; private set; }
            public double RequiredSeparationMm { get; private set; }

            public static IsocenterConfiguration CreateSingle(IsoPoint single, double targetLengthMm, double requiredSeparationMm)
            {
                return new IsocenterConfiguration
                {
                    UseDualIsocenter = false,
                    Single = single,
                    TargetLengthMm = targetLengthMm,
                    RequiredSeparationMm = requiredSeparationMm,
                };
            }

            public static IsocenterConfiguration CreateDual(IsoPoint superior, IsoPoint inferior, double targetLengthMm, double requiredSeparationMm)
            {
                return new IsocenterConfiguration
                {
                    UseDualIsocenter = true,
                    Superior = superior,
                    Inferior = inferior,
                    TargetLengthMm = targetLengthMm,
                    RequiredSeparationMm = requiredSeparationMm,
                };
            }
        }
    }
}

