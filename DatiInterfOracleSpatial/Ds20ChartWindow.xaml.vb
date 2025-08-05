
Imports DatiInterfOracleSpatial
Imports OxyPlot
Imports OxyPlot.Series
Namespace DatiInterfOracleSpatial
    Public Class Ds20ChartWindow
        ''' <summary>
        ''' labels: etichette X (es. D20190524)
        ''' values: valori Y
        ''' meanVel: valore medio (MEAN_VEL) del punto selezionato, usato per il colore unico dei marker
        ''' </summary>
        Public Sub New(labels As List(Of String), values As List(Of Double), meanVel As Double, infoPunto As String)
            InitializeComponent()

            Dim plotModel = New PlotModel With {
        .Title = "Serie Storica",
        .Subtitle = infoPunto,
        .IsLegendVisible = True
    }

            ' === ASSE Y: aggiungi unità di misura "mm" sia nel titolo che nelle etichette numeriche ===
            plotModel.Axes.Add(New OxyPlot.Axes.LinearAxis With {
            .Position = OxyPlot.Axes.AxisPosition.Left,
            .Title = "Spostamento (mm)",
            .StringFormat = "0 mm",
            .MajorGridlineStyle = LineStyle.Solid,
            .MinorGridlineStyle = LineStyle.Dot,
            .MajorGridlineColor = OxyColors.LightGray,
            .MinorGridlineColor = OxyColors.Gainsboro
        })

            ' === ASSE X: DateTimeAxis per intervalli temporali reali ===
            Dim dateList As New List(Of DateTime)
            For Each label In labels
                If Not String.IsNullOrEmpty(label) AndAlso label.StartsWith("D2") AndAlso label.Length >= 9 Then
                    Dim year = Integer.Parse(label.Substring(1, 4))
                    Dim month = Integer.Parse(label.Substring(5, 2))
                    Dim day = Integer.Parse(label.Substring(7, 2))
                    dateList.Add(New DateTime(year, month, day))
                Else
                    dateList.Add(DateTime.MinValue)
                End If
            Next

            Dim validDates = dateList.Where(Function(d) d <> DateTime.MinValue).ToList()
            Dim minDate As DateTime = DateTime.MinValue
            Dim maxDate As DateTime = DateTime.MinValue
            If validDates.Count > 0 Then
                minDate = validDates.Min()
                maxDate = validDates.Max()
            End If

            ' Padding ai bordi per non tagliare i punti
            Dim paddingDays As Integer = 10
            Dim minDatePadded = minDate.AddDays(-paddingDays)
            Dim maxDatePadded = maxDate.AddDays(paddingDays)

            plotModel.Axes.Add(New OxyPlot.Axes.DateTimeAxis With {
            .Position = OxyPlot.Axes.AxisPosition.Bottom,
            .StringFormat = "yyyy/MM", ' Mostra solo anno/mese
            .Title = "Data",
            .Angle = 80,
            .FontSize = 11,
            .Font = "Arial Narrow",
            .Minimum = OxyPlot.Axes.DateTimeAxis.ToDouble(minDatePadded),
            .Maximum = OxyPlot.Axes.DateTimeAxis.ToDouble(maxDatePadded),
            .MajorStep = 30, ' Circa un mese tra le etichette principali
            .IntervalType = OxyPlot.Axes.DateTimeIntervalType.Days,
            .MajorGridlineStyle = LineStyle.Solid,
            .MinorGridlineStyle = LineStyle.Dot,
            .MajorGridlineColor = OxyColors.LightGray,
            .MinorGridlineColor = OxyColors.Gainsboro
        })

            ' === SERIE DATI: scatter plot ===

            ' --- SOLUZIONE 1: colore unico per tutti i punti, in base al valore medio (MEAN_VEL) ---
            ' Questa soluzione usa il colore coerente con la mappa per il punto selezionato.
            ' Tutti i marker avranno lo stesso colore calcolato da ColorUtils.CalcolaColoreMarker(meanVel).
            '
            'Dim markerColor = ColorUtils.CalcolaColoreMarker(meanVel)
            'Dim oxyColor = OxyPlot.OxyColor.FromArgb(
            '     CByte(markerColor.Alpha),
            '     CByte(markerColor.R),
            '     CByte(markerColor.G),
            '     CByte(markerColor.B)
            ' )
            'Dim series = New ScatterSeries With {
            '     .Title = "Valori",
            '     .MarkerType = MarkerType.Circle,
            '     .MarkerSize = 4,
            '     .MarkerFill = oxyColor,
            '     .ToolTip = "Data: {2:yyyy/MM/dd}" & vbCrLf & "Valore: {4:0.00} mm"
            ' }
            'For i = 0 To values.Count - 1
            '    If dateList(i) <> DateTime.MinValue Then
            '        series.Points.Add(New ScatterPoint(
            '             OxyPlot.Axes.DateTimeAxis.ToDouble(dateList(i)),
            '             values(i),
            '             4
            '         ))
            '    End If
            'Next
            'plotModel.Series.Add(series)

            ' --- SOLUZIONE 2: colore per ogni punto in base al valore (allineato a ColorUtils) ---
            ' Crea una serie per ogni colore diverso, così ogni punto avrà lo stesso colore della mappa.
            'Dim puntiPerColore = New Dictionary(Of String, List(Of Integer)) ' chiave: colore, valore: indici dei punti
            'For i = 0 To values.Count - 1
            '    If dateList(i) <> DateTime.MinValue Then
            '        Dim c = ColorUtils.CalcolaColoreMarker(values(i))
            '        Dim key = $"{c.R}-{c.G}-{c.B}-{c.Alpha}"
            '        If Not puntiPerColore.ContainsKey(key) Then
            '            puntiPerColore(key) = New List(Of Integer)
            '        End If
            '        puntiPerColore(key).Add(i)
            '    End If
            'Next
            '
            'For Each kvp In puntiPerColore
            '    Dim colorParts = kvp.Key.Split("-"c).Select(Function(s) Byte.Parse(s)).ToArray()
            '    Dim oxyColor = OxyPlot.OxyColor.FromArgb(colorParts(3), colorParts(0), colorParts(1), colorParts(2))
            '    Dim series = New ScatterSeries With {
            '        .Title = "Valori",
            '        .MarkerType = MarkerType.Circle,
            '        .MarkerSize = 4,
            '        .MarkerFill = oxyColor,
            '        .ToolTip = "Data: {2:yyyy/MM/dd}" & vbCrLf & "Valore: {4:0.00} mm"
            '    }
            '    For Each idx In kvp.Value
            '        series.Points.Add(New ScatterPoint(
            '            OxyPlot.Axes.DateTimeAxis.ToDouble(dateList(idx)),
            '            values(idx),
            '            4
            '        ))
            '    Next
            '    plotModel.Series.Add(series)
            'Next


            ' --- SOLUZIONE 3: scala di colori rainbow dinamica ---
            ' Dim minValue As Double = -5
            ' Dim maxValue As Double = 5
            '
            ' For i = 0 To values.Count - 1
            '     If dateList(i) <> DateTime.MinValue Then
            '         Dim oxyColor = ColorUtils.CalcolaColoreOxyRainbow(values(i), minValue, maxValue)
            '         Dim singleSeries = New ScatterSeries With {
            '         .Title = If(i = 0, "Valori (Rainbow)", Nothing), ' Solo la prima serie ha titolo
            '         .MarkerType = MarkerType.Circle,
            '         .MarkerSize = 4,
            '         .MarkerFill = oxyColor,
            '         .ToolTip = "Data: {2:yyyy/MM/dd}" & vbCrLf & "Valore: {4:0.00} mm"
            '     }
            '         singleSeries.Points.Add(New ScatterPoint(
            '         OxyPlot.Axes.DateTimeAxis.ToDouble(dateList(i)),
            '         values(i),
            '         4
            '     ))
            '         plotModel.Series.Add(singleSeries)
            '     End If
            ' Next
            ' --- SOLUZIONE 4: tutti i punti con colore rainbow in base a meanVStd ---
            ' Imposta qui il range della scala rainbow (modifica se necessario)
            Dim minValue As Double = -5
            Dim maxValue As Double = 5

            ' Calcola il colore rainbow per il valore medio MEAN_V_STD
            Dim markerColor = ColorUtils.CalcolaColoreRainbow(meanVel, minValue, maxValue)
            Dim oxyColor = OxyPlot.OxyColor.FromArgb(
            CByte(markerColor.Alpha),
            CByte(markerColor.R),
            CByte(markerColor.G),
            CByte(markerColor.B)
        )

            Dim series = New ScatterSeries With {
            .Title = "Valori",
            .MarkerType = MarkerType.Circle,
            .MarkerSize = 4,
            .MarkerFill = oxyColor,
            .ToolTip = "Data: {2:yyyy/MM/dd}" & vbCrLf & "Valore: {4:0.00} mm"
        }
            For i = 0 To values.Count - 1
                If dateList(i) <> DateTime.MinValue Then
                    series.Points.Add(New ScatterPoint(
                    OxyPlot.Axes.DateTimeAxis.ToDouble(dateList(i)),
                    values(i),
                    4
                ))
                End If
            Next
            plotModel.Series.Add(series)

            ' === SERIE REGRESSIONE LINEARE ===
            ' Calcolo regressione lineare sui dati temporali (solo se almeno 2 punti validi)
            Dim validPoints = dateList.Select(Function(d, idx) New With {.Date = d, .Value = values(idx)}).Where(Function(x) x.Date <> DateTime.MinValue).ToList()
            If validPoints.Count > 1 Then
                Dim n = validPoints.Count
                Dim sumX = validPoints.Sum(Function(x) OxyPlot.Axes.DateTimeAxis.ToDouble(x.Date))
                Dim sumY = validPoints.Sum(Function(x) x.Value)
                Dim sumXY = validPoints.Sum(Function(x) OxyPlot.Axes.DateTimeAxis.ToDouble(x.Date) * x.Value)
                Dim sumX2 = validPoints.Sum(Function(x) Math.Pow(OxyPlot.Axes.DateTimeAxis.ToDouble(x.Date), 2))
                Dim denom = n * sumX2 - sumX * sumX
                Dim m As Double = 0
                Dim q As Double = 0
                If denom <> 0 Then
                    m = (n * sumXY - sumX * sumY) / denom
                    q = (sumY * sumX2 - sumX * sumXY) / denom
                End If

                ' Serie della retta di regressione
                Dim regression = New LineSeries With {
                .Title = "Regressione lineare",
                .Color = OxyColors.Red,
                .StrokeThickness = 2,
                .LineStyle = LineStyle.Dash
            }
                For Each p In validPoints
                    Dim x = OxyPlot.Axes.DateTimeAxis.ToDouble(p.Date)
                    regression.Points.Add(New DataPoint(x, m * x + q))
                Next
                plotModel.Series.Add(regression)
            End If

            PlotView.Model = plotModel
        End Sub







    End Class

End Namespace