// Controlled local subprocess for JsonlBridge fault tests; no NX or pipe access.
using System;
using System.Threading;
public static class F21FakeJsonlHelper
{
    public static int Main()
    {
        string line=Console.ReadLine();if(line==null)return 0;
        string mode=Environment.GetEnvironmentVariable("F21_FAKE_HELPER_MODE")??"pending";
        if(mode=="timeout"){Thread.Sleep(2000);return 0;}
        if(mode=="eof")return 0;
        if(mode=="badjson")Console.WriteLine("not-json");
        else if(mode=="badid")Console.WriteLine("{\"id\":\"wrong\",\"result\":{}}");
        else if(mode=="badresult")Console.WriteLine("{\"id\":\"1\",\"result\":[]}");
        else if(mode=="baderror")Console.WriteLine("{\"id\":\"1\",\"error\":[]}");
        else if(mode=="badenv")Console.WriteLine("{\"id\":\"1\",\"error\":{},\"result\":{}}");
        else if(mode=="backend")Console.WriteLine("{\"id\":\"1\",\"error\":{\"code\":\"INVALID_REQUEST\",\"message\":\"rejected\"}}");
        else Console.WriteLine("{\"id\":\"1\",\"result\":{\"state\":\"QUEUED\",\"operation_id\":\"fake\"}}");
        Console.Out.Flush();return 0;
    }
}
