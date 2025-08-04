Imports DatiInterfOracleSpatial
Namespace DatiInterfOracleSpatial
    Public Class MarkerColor
        Public Property R As Integer
        Public Property G As Integer
        Public Property B As Integer
        Public Property Alpha As Integer
        Public Function ToJsArray() As String
            Return $"[{R}, {G}, {B}, {Alpha}]"
        End Function
    End Class
End Namespace