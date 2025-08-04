Imports DatiInterfOracleSpatial
Namespace DatiInterfOracleSpatial
    Public NotInheritable Class ColorUtils
        Private Sub New()
        End Sub
        ' === Soluzione 1: Scala di colori fissa ===
        Public Shared Function CalcolaColoreMarker(valore As Double) As MarkerColor
            If Double.IsNaN(valore) Then
                Return New MarkerColor With {.R = 128, .G = 128, .B = 128, .Alpha = 255}
            End If

            If valore >= 4.5 Then
                Return New MarkerColor With {.R = 13, .G = 25, .B = 78, .Alpha = 255}
            ElseIf valore >= 3.5 Then
                Return New MarkerColor With {.R = 11, .G = 56, .B = 81, .Alpha = 255}
            ElseIf valore >= 2.5 Then
                Return New MarkerColor With {.R = 0, .G = 83, .B = 78, .Alpha = 255}
            ElseIf valore >= 1.5 Then
                Return New MarkerColor With {.R = 0, .G = 85, .B = 50, .Alpha = 255}
            ElseIf valore >= 0.5 Then
                Return New MarkerColor With {.R = 0, .G = 87, .B = 24, .Alpha = 255}
            ElseIf valore >= -0.5 Then
                Return New MarkerColor With {.R = 0, .G = 89, .B = 17, .Alpha = 255}
            ElseIf valore >= -1.5 Then
                Return New MarkerColor With {.R = 47, .G = 92, .B = 19, .Alpha = 255}
            ElseIf valore >= -2.5 Then
                Return New MarkerColor With {.R = 83, .G = 94, .B = 21, .Alpha = 255}
            ElseIf valore >= -3.5 Then
                Return New MarkerColor With {.R = 95, .G = 71, .B = 18, .Alpha = 255}
            ElseIf valore >= -4.5 Then
                Return New MarkerColor With {.R = 97, .G = 39, .B = 13, .Alpha = 255}
            Else
                Return New MarkerColor With {.R = 100, .G = 16, .B = 12, .Alpha = 255}
            End If
        End Function

        ' Utility per OxyPlot
        Public Shared Function CalcolaColoreOxy(valore As Double) As OxyPlot.OxyColor
            Dim c = CalcolaColoreMarker(valore)
            Return OxyPlot.OxyColor.FromArgb(CByte(c.Alpha), CByte(c.R), CByte(c.G), CByte(c.B))
        End Function


        ' === Soluzione 3: Scala di colori rainbow dinamica ===
        Public Shared Function CalcolaColoreRainbow(valore As Double, min As Double, max As Double, Optional alpha As Integer = 255) As MarkerColor
            If Double.IsNaN(valore) OrElse max = min Then
                Return New MarkerColor With {.R = 128, .G = 128, .B = 128, .Alpha = alpha}
            End If

            ' Normalizza tra 0 e 1
            Dim t = Math.Max(0, Math.Min(1, (valore - min) / (max - min)))
            ' Hue da 240 (blu) a 0 (rosso)
            Dim hue = 240.0 - 240.0 * t
            Dim s = 1.0
            Dim l = 0.5

            ' Conversione HSL -> RGB
            Dim c = (1 - Math.Abs(2 * l - 1)) * s
            Dim x = c * (1 - Math.Abs((hue / 60) Mod 2 - 1))
            Dim m = l - c / 2
            Dim r1 = 0.0, g1 = 0.0, b1 = 0.0

            If hue < 60 Then
                r1 = c : g1 = x : b1 = 0
            ElseIf hue < 120 Then
                r1 = x : g1 = c : b1 = 0
            ElseIf hue < 180 Then
                r1 = 0 : g1 = c : b1 = x
            ElseIf hue < 240 Then
                r1 = 0 : g1 = x : b1 = c
            ElseIf hue < 300 Then
                r1 = x : g1 = 0 : b1 = c
            Else
                r1 = c : g1 = 0 : b1 = x
            End If

            Dim R = CInt((r1 + m) * 255)
            Dim G = CInt((g1 + m) * 255)
            Dim B = CInt((b1 + m) * 255)

            Return New MarkerColor With {.R = R, .G = G, .B = B, .Alpha = alpha}
        End Function

        Public Shared Function CalcolaColoreOxyRainbow(valore As Double, min As Double, max As Double, Optional alpha As Integer = 255) As OxyPlot.OxyColor
            Dim c = CalcolaColoreRainbow(valore, min, max, alpha)
            Return OxyPlot.OxyColor.FromArgb(CByte(c.Alpha), CByte(c.R), CByte(c.G), CByte(c.B))
        End Function

    End Class
End Namespace