Imports Microsoft.Win32
Imports System.Windows

' MainWindow: Solo gestione overlay e dipendenze minime
Class MainWindow

    ' === EVENTO DI CARICAMENTO FINESTRA ===
    Private Sub Window_Loaded(sender As Object, e As RoutedEventArgs) Handles MyBase.Loaded
        MessageBox.Show("Gestione overlay attiva.", "Info", MessageBoxButton.OK, MessageBoxImage.Information)
    End Sub



    ' === APRI FINESTRA OVERLAY ===
    Private Sub BtnGeoOverlay_Click(sender As Object, e As RoutedEventArgs)
        Try
            Dim geoOverlayWindow As New DatiInterfOracleSpatial.GeoDataOverlayWindow()
            geoOverlayWindow.Show()
        Catch ex As Exception
            MessageBox.Show($"Errore nell'apertura della finestra Geo Overlay: {ex.Message}", "Errore", MessageBoxButton.OK, MessageBoxImage.Error)
        End Try
    End Sub

End Class