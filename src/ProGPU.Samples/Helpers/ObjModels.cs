using System;
using System.IO;
using System.Text;

namespace ProGPU.Samples
{
    public static class ObjModels
    {
        public static string GenerateSpacecraftObj()
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Procedural Spacecraft OBJ model");

            // Vertices
            sb.AppendLine("v 0.0 0.0 3.0"); // 1: Nose tip
            sb.AppendLine("v 0.0 0.4 1.5");  // 2
            sb.AppendLine("v -0.4 -0.2 1.5"); // 3
            sb.AppendLine("v 0.4 -0.2 1.5");  // 4

            sb.AppendLine("v 0.0 0.5 0.0");  // 5
            sb.AppendLine("v -0.5 -0.3 0.0"); // 6
            sb.AppendLine("v 0.5 -0.3 0.0");  // 7

            sb.AppendLine("v 0.0 0.4 -2.0");  // 8
            sb.AppendLine("v -0.4 -0.2 -2.0"); // 9
            sb.AppendLine("v 0.4 -0.2 -2.0");  // 10
            sb.AppendLine("v 0.0 0.0 -2.5");   // 11: Engine nozzle tip

            sb.AppendLine("v -0.5 -0.2 0.5");  // 12: Wing joint front
            sb.AppendLine("v -2.5 -0.4 -1.5"); // 13: Wing tip
            sb.AppendLine("v -0.4 -0.2 -1.5"); // 14: Wing joint rear

            sb.AppendLine("v 0.5 -0.2 0.5");   // 15: Wing joint front
            sb.AppendLine("v 2.5 -0.4 -1.5");  // 16: Wing tip
            sb.AppendLine("v 0.4 -0.2 -1.5");   // 17: Wing joint rear

            sb.AppendLine("v 0.0 0.4 -0.5");   // 18: Fin joint front
            sb.AppendLine("v 0.0 1.5 -1.8");   // 19: Fin tip
            sb.AppendLine("v 0.0 0.4 -1.8");   // 20: Fin joint rear

            // Faces
            // Nose Cone to Cockpit
            sb.AppendLine("f 1 2 3");
            sb.AppendLine("f 1 3 4");
            sb.AppendLine("f 1 4 2");

            // Cockpit to Fuselage
            sb.AppendLine("f 2 5 6");
            sb.AppendLine("f 2 6 3");
            sb.AppendLine("f 3 6 7");
            sb.AppendLine("f 3 7 4");
            sb.AppendLine("f 4 7 5");
            sb.AppendLine("f 4 5 2");

            // Fuselage to Engines
            sb.AppendLine("f 5 8 9");
            sb.AppendLine("f 5 9 6");
            sb.AppendLine("f 6 9 10");
            sb.AppendLine("f 6 10 7");
            sb.AppendLine("f 7 10 8");
            sb.AppendLine("f 7 8 5");

            // Engine Nozzle Cone
            sb.AppendLine("f 8 11 9");
            sb.AppendLine("f 9 11 10");
            sb.AppendLine("f 10 11 8");

            // Left Wing (Double Sided)
            sb.AppendLine("f 12 13 14");
            sb.AppendLine("f 12 14 13");

            // Right Wing (Double Sided)
            sb.AppendLine("f 15 17 16");
            sb.AppendLine("f 15 16 17");

            // Tail Fin (Double Sided)
            sb.AppendLine("f 18 19 20");
            sb.AppendLine("f 18 20 19");

            return sb.ToString();
        }

        public static string GenerateFacetedJewelObj()
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Procedural Faceted Jewel OBJ model");

            int slices = 16;

            // Center ring
            for (int i = 0; i < slices; i++)
            {
                float angle = (float)i / slices * MathF.PI * 2.0f;
                sb.AppendLine($"v {MathF.Cos(angle) * 2.0f} 0.0 {MathF.Sin(angle) * 2.0f}"); // 1 to slices
            }

            // Upper ring
            for (int i = 0; i < slices; i++)
            {
                float angle = ((float)i + 0.5f) / slices * MathF.PI * 2.0f;
                sb.AppendLine($"v {MathF.Cos(angle) * 1.2f} 0.8 {MathF.Sin(angle) * 1.2f}"); // slices+1 to 2*slices
            }

            // Top vertex
            sb.AppendLine("v 0.0 1.5 0.0"); // 2*slices + 1

            // Bottom vertex
            sb.AppendLine("v 0.0 -2.0 0.0"); // 2*slices + 2

            // Faces
            // Upper cap triangles
            int topIdx = 2 * slices + 1;
            for (int i = 0; i < slices; i++)
            {
                int next = (i + 1) % slices;
                sb.AppendLine($"f {topIdx} {slices + 1 + i} {slices + 1 + next}");
            }

            // Upper band quads (triangulated)
            for (int i = 0; i < slices; i++)
            {
                int next = (i + 1) % slices;
                int v0 = i + 1;
                int v1 = next + 1;
                int v2 = slices + 1 + i;
                int v3 = slices + 1 + next;

                sb.AppendLine($"f {v0} {v1} {v3}");
                sb.AppendLine($"f {v0} {v3} {v2}");
            }

            // Lower band triangles to bottom tip
            int bottomIdx = 2 * slices + 2;
            for (int i = 0; i < slices; i++)
            {
                int next = (i + 1) % slices;
                sb.AppendLine($"f {bottomIdx} {next + 1} {i + 1}");
            }

            return sb.ToString();
        }

        public static void EnsureSamplesExist(string targetDirectory)
        {
            if (!Directory.Exists(targetDirectory))
            {
                Directory.CreateDirectory(targetDirectory);
            }

            string spacecraftPath = Path.Combine(targetDirectory, "spacecraft.obj");
            if (!File.Exists(spacecraftPath))
            {
                File.WriteAllText(spacecraftPath, GenerateSpacecraftObj());
            }

            string jewelPath = Path.Combine(targetDirectory, "jewel.obj");
            if (!File.Exists(jewelPath))
            {
                File.WriteAllText(jewelPath, GenerateFacetedJewelObj());
            }
        }
    }
}
