Imports System.Data
Imports Oracle.ManagedDataAccess.Client

Public Class OracleConnectionHelper
    Private ReadOnly _connectionString As String

    Public Sub New()
        _connectionString = "User Id=dwhadm;Password=dwhadm;" &
                            "Data Source=(DESCRIPTION=(ADDRESS_LIST=(ADDRESS=(PROTOCOL=TCP)(HOST=ecsnp-zjydh-scan.snvcnnplindbcli.vcnnplin.oraclevcn.com)(PORT=1521)))" &
                            "(CONNECT_DATA=(SERVICE_NAME=dwhsvil)))"
    End Sub

    Public Function GetConnection() As OracleConnection
        Return New OracleConnection(_connectionString)
    End Function

    Public Function ExecuteQuery(query As String, Optional parameters As Dictionary(Of String, Object) = Nothing) As DataTable
        Dim dt As New DataTable()
        Try
            Debug.WriteLine($"[OracleConnectionHelper] Esecuzione query: {query}")
            If parameters IsNot Nothing Then
                For Each kvp In parameters
                    Debug.WriteLine($"[OracleConnectionHelper] Parametro: {kvp.Key} = {kvp.Value}")
                Next
            End If
            Using conn As OracleConnection = GetConnection()
                conn.Open()
                Using cmd As New OracleCommand(query, conn)
                    cmd.CommandTimeout = 200 ' Timeout aumentato a 60 secondi
                    If parameters IsNot Nothing Then
                        For Each kvp In parameters
                            cmd.Parameters.Add(New OracleParameter(kvp.Key, kvp.Value))
                        Next
                    End If
                    Using da As New OracleDataAdapter(cmd)
                        da.Fill(dt)
                    End Using
                End Using
            End Using
        Catch ex As OracleException
            Debug.WriteLine($"[OracleConnectionHelper] ORACLE ERROR: {ex.Message} - Codice: {ex.Number}")
            Debug.WriteLine($"StackTrace: {ex.StackTrace}")
            Throw
        Catch ex As Exception
            Debug.WriteLine($"[OracleConnectionHelper] ERRORE ExecuteQuery: {ex.Message} - {ex.GetType().FullName}")
            Debug.WriteLine($"StackTrace: {ex.StackTrace}")
            Throw
        End Try
        Return dt
    End Function

    ' Public Function ExecuteNonQuery(query As String, Optional parameters As Dictionary(Of String, Object) = Nothing) As Integer
    '     Try
    '         Using conn As OracleConnection = GetConnection()
    '             conn.Open()
    '             Using cmd As New OracleCommand(query, conn)
    '                 cmd.CommandTimeout = 60
    '                 If parameters IsNot Nothing Then
    '                     For Each kvp In parameters
    '                         cmd.Parameters.Add(New OracleParameter(kvp.Key, kvp.Value))
    '                     Next
    '                 End If
    '                 Return cmd.ExecuteNonQuery()
    '             End Using
    '         End Using
    '     Catch ex As OracleException
    '         Debug.WriteLine($"[OracleConnectionHelper] ORACLE ERROR: {ex.Message} - Codice: {ex.Number}")
    '         Debug.WriteLine($"StackTrace: {ex.StackTrace}")
    '         Throw
    '     Catch ex As Exception
    '         Debug.WriteLine($"[OracleConnectionHelper] ERRORE ExecuteNonQuery: {ex.Message} - {ex.GetType().FullName}")
    '         Debug.WriteLine($"StackTrace: {ex.StackTrace}")
    '         Throw
    '     End Try
    ' End Function

    Public Function GetDistinctValuesMulti(tableName As String, columnName As String, filtri As Dictionary(Of String, List(Of String))) As List(Of String)
        Dim result As New List(Of String)
        Dim whereList As New List(Of String)
        For Each kvp In filtri
            If kvp.Value IsNot Nothing AndAlso kvp.Value.Count > 0 Then
                Dim inClause = String.Join(",", kvp.Value.Select(Function(v) $"'{v.Replace("'", "''")}'"))
                whereList.Add($"{kvp.Key} IN ({inClause})")
            End If
        Next
        Dim whereClause = If(whereList.Count > 0, " WHERE " & String.Join(" AND ", whereList), "")
        Dim query = $"SELECT DISTINCT {columnName} FROM {tableName}{whereClause} ORDER BY {columnName}"
        Dim dt = ExecuteQuery(query)
        For Each row As DataRow In dt.Rows
            result.Add(row(0).ToString())
        Next
        Return result
    End Function
End Class