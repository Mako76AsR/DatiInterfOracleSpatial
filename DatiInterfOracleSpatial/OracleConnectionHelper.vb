Imports System.Data
Imports Oracle.ManagedDataAccess.Client

Public Class OracleConnectionHelper
    Private ReadOnly _connectionString As String

    Public Sub New()
        ' Costruisce la stringa di connessione Oracle
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
            Using conn As OracleConnection = GetConnection()
                conn.Open()
                Using cmd As New OracleCommand(query, conn)
                    cmd.CommandTimeout = 30 ' Timeout di 30 secondi
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
        Catch ex As Exception
            ' Log dell'errore (puoi sostituire con il tuo sistema di log)
            Debug.WriteLine($"[OracleConnectionHelper] ERRORE ExecuteQuery: {ex.Message}")
            Throw
        End Try
        Return dt
    End Function

    Public Function ExecuteNonQuery(query As String, Optional parameters As Dictionary(Of String, Object) = Nothing) As Integer
        Try
            Using conn As OracleConnection = GetConnection()
                conn.Open()
                Using cmd As New OracleCommand(query, conn)
                    cmd.CommandTimeout = 30 ' Timeout di 30 secondi
                    If parameters IsNot Nothing Then
                        For Each kvp In parameters
                            cmd.Parameters.Add(New OracleParameter(kvp.Key, kvp.Value))
                        Next
                    End If
                    Return cmd.ExecuteNonQuery()
                End Using
            End Using
        Catch ex As Exception
            Debug.WriteLine($"[OracleConnectionHelper] ERRORE ExecuteNonQuery: {ex.Message}")
            Throw
        End Try
    End Function
End Class