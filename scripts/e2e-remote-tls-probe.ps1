$ErrorActionPreference='Stop'
Add-Type -TypeDefinition @'
using System;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
public static class E2eTlsProbe {
 public static string Run() {
  using(var tcp=new TcpClient()) {
   var connect=tcp.ConnectAsync("192.168.0.61",25001);
   if(!connect.Wait(3000))throw new TimeoutException("TCP25001 connect timeout");
   using(var tls=new SslStream(tcp.GetStream(),false,(sender,certificate,chain,errors)=> {
    if(certificate==null||(errors&SslPolicyErrors.RemoteCertificateNameMismatch)!=0)return false;
    using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(certificate.GetRawCertData())).Replace("-","").Equals("45EEBA84A6D2E5E30EC083C4E1981F6152C25625A0FF28E4F1E314A70087AED2",StringComparison.OrdinalIgnoreCase);
   })) {
    if(!tls.AuthenticateAsClientAsync("192.168.0.61").Wait(5000))throw new TimeoutException("TLS timeout");
    tls.ReadTimeout=3000;tls.WriteTimeout=3000;
    var request=Encoding.ASCII.GetBytes("GET /healthz HTTP/1.1\r\nHost: 192.168.0.61:25001\r\nConnection: close\r\n\r\n");
    tls.Write(request,0,request.Length);var bytes=new byte[1024];var count=tls.Read(bytes,0,bytes.Length);
    var response=Encoding.ASCII.GetString(bytes,0,count);
    if(!response.StartsWith("HTTP/1.0 200"))throw new Exception("Unexpected probe response");
    return "PASS: pinned temporary certificate and IP name validated over LAN; OS trust unchanged; no product session";
   }
  }
 }
}
'@
[E2eTlsProbe]::Run()
