﻿using System;
using System.Collections.Generic;
using System.Threading;

namespace RFD.RFD900
{
    /// <summary>
    /// For detecting which mode the modem is in, and putting it in to desired modes.
    /// </summary>
    public class TSession
    {
        TMode _Mode = TMode.INIT;
        MissionPlanner.Comms.ICommsSerial _Port;
        const int BOOTLOADER_BAUD = 115200;

        public TSession(MissionPlanner.Comms.ICommsSerial Port)
        {
            _Port = Port;
        }

        public enum TMode
        {
            INIT,
            TRANSPARENT,
            AT_COMMAND,
            BOOTLOADER,     //For a,u,+
            BOOTLOADER_X,    //For x
            UNKNOWN
        }

        public bool WaitForToken(string Token, int MaxWait)
        {
            System.Diagnostics.Stopwatch SW = new System.Diagnostics.Stopwatch();
            string Temp = "";
            int Timeout = _Port.ReadTimeout;
            _Port.ReadTimeout = MaxWait;
            SW.Start();

            while (SW.ElapsedMilliseconds < MaxWait)
            {
                string x = _Port.ReadExisting();
                Temp += x;

                if (Temp.Contains(Token))
                {
                    _Port.ReadTimeout = Timeout;
                    return true;
                }

                Thread.Sleep(50);
            }
            _Port.ReadTimeout = Timeout;
            return false;
        }

        void WriteBootloaderCode(uploader.Uploader.Code Code)
        {
            byte[] ToSend = new byte[] { (byte)Code };
            _Port.Write(ToSend, 0, 1);
        }

        bool IsInBootloaderMode()
        {
            int PrevBaud = _Port.BaudRate;
            _Port.BaudRate = BOOTLOADER_BAUD;
            Thread.Sleep(100);
            WriteBootloaderCode(uploader.Uploader.Code.EOC);
            Thread.Sleep(100);
            _Port.DiscardInBuffer();
            WriteBootloaderCode(uploader.Uploader.Code.GET_SYNC);
            WriteBootloaderCode(uploader.Uploader.Code.EOC);
            bool Result;
            try
            {
                Result = (_Port.ReadByte() == (int)uploader.Uploader.Code.INSYNC);
            }
            catch
            {
                Result = false;
            }
            if (!Result)
            {
                _Port.BaudRate = PrevBaud;
            }
            return Result;
        }

        /// <summary>
        /// TODO - determine whether bootloader is 0.0 or 0.1, as in Matheus's code.
        /// </summary>
        /// <returns></returns>
        bool IsInBootloaderXMode()
        {
            //for (int n = 0; n < 3; n++)
            {
                Thread.Sleep(200);
                _Port.Write("U");   //Send it a capital U to sync the baud rate
                Thread.Sleep(200);
                _Port.Write("\r\n");
                Thread.Sleep(200);
                _Port.DiscardInBuffer();
                _Port.Write("CHIPID\r\n");
                if (WaitForToken("RFD", 200))
                {
                    return true;
                }
            }
            return false;
        }

        TMode DetermineMode()
        {
            if (IsInBootloaderXMode())
            {
                return TMode.BOOTLOADER_X;
            }

            _Port.ReadTimeout = 2000;
            //Console.WriteLine("Waiting 1500ms");
            Thread.Sleep(1500);
            _Port.DiscardInBuffer();
            //Console.WriteLine("Sending +++");
            _Port.Write("+++");
            //Console.WriteLine("Waiting up to 3s for OK");
            if (WaitForToken("OK\r\n", 3000))
            {
                return TMode.AT_COMMAND;
            }
            //Check if already in AT command mode.
            _Port.DiscardInBuffer();
            _Port.Write("\r\n");
            Thread.Sleep(100);
            _Port.Write("ATI\r\n");
            if (WaitForToken("RFD", 400))
            {
                return TMode.AT_COMMAND;
            }
            //Not in transparent or at command mode.  Probably in bootloader mode.  Need to verify.
            if (IsInBootloaderXMode())
            {
                return TMode.BOOTLOADER_X;
            }
            if (IsInBootloaderMode())
            {
                return TMode.BOOTLOADER;
            }
            return TMode.UNKNOWN;
        }

        void CheckIfInBootloaderMode()
        {
            if (IsInBootloaderXMode())
            {
                _Mode = TMode.BOOTLOADER_X;
            }
            else if (IsInBootloaderMode())
            {
                _Mode = TMode.BOOTLOADER;
            }
        }

        public MissionPlanner.Comms.ICommsSerial Port
        {
            get
            {
                return _Port;
            }
        }

        public TMode GetMode()
        {
            if (_Mode == TMode.INIT)
            {
                _Mode = DetermineMode();
            }
            return _Mode;
        }

        public TMode PutIntoBootloaderMode()
        {
            var CurrentMode = GetMode();
            if (CurrentMode == TMode.AT_COMMAND)
            {
                _Port.Write("\r\n");
                Thread.Sleep(100);
                _Port.Write("AT&UPDATE\r\n");
                Thread.Sleep(100);
                CheckIfInBootloaderMode();
            }
            return GetMode();
        }

        public TMode PutIntoATCommandMode()
        {
            TMode Current = GetMode();
            switch (Current)
            {
                case TMode.BOOTLOADER:
                    int PrevBaud = _Port.BaudRate;
                    _Port.BaudRate = BOOTLOADER_BAUD;
                    WriteBootloaderCode(uploader.Uploader.Code.REBOOT);
                    _Port.BaudRate = PrevBaud;
                    _Mode = TMode.INIT;
                    break;
                case TMode.BOOTLOADER_X:
                    // boot
                    Thread.Sleep(100);
                    _Port.Write("\r\n");
                    Thread.Sleep(100);
                    _Port.Write("BOOTNEW\r\n");
                    Thread.Sleep(100);
                    _Mode = TMode.INIT;
                    break;
            }
            return GetMode();
        }

        /// <summary>
        /// Parse the setting designator from the given Line and character positon.
        /// </summary>
        /// <param name="Line">The ATI5? line.  Must not be null.</param>
        /// <param name="n">The current character position.</param>
        /// <returns></returns>
        string ParseDesignator(string Line, ref int n)
        {
            n = 0;
            string Designator = "";

            if (RFDLib.Text.CheckIsLetter(Line[0]))
            {
                Designator += Line[0];
            }
            else
            {
                return null;
            }

            n = 1;

            while (Line[n] != ':')
            {
                if (RFDLib.Text.CheckIsNumeral(Line[n]))
                {
                    Designator += Line[n];
                }
                else
                {
                    return null;
                }
                n++;
            }

            return Designator;
        }

        string ParseName(string Line, ref int n)
        {
            string Name = "";
            while (true)
            {
                switch (Line[n])
                {
                    case '(':
                    case '=':
                        return Name;
                }

                Name += Line[n];
                n++;
            }
        }

        bool ParseType(string Line, ref int n)
        {
            for (; Line[n] != ')'; n++)
            {
            }
            n++;
            return true;
        }

        bool ParseIntUntil(string Line, ref int n, string Delimiter, out int Value)
        {
            string Temp = "";
            while ((n < Line.Length) && RFDLib.Text.CheckIsNumeral(Line[n]))
            {
                Temp += Line[n];
                n++;
            }
            Value = int.Parse(Temp);
            if (Delimiter == null)
            {
                return true;
            }
            else
            {
                if (Line.IndexOf(Delimiter, n) == n)
                {
                    n += Delimiter.Length;
                    return true;
                }
                else
                {
                    return false;
                }
            }
        }

        TSetting.TRange ParseRange(string Line, ref int n)
        {
            if (Line[n] != '[')
            {
                return null;
            }
            n++;
            int Min;
            if (!ParseIntUntil(Line, ref n, "..", out Min))
            {
                return null;
            }
            int Max;
            if (!ParseIntUntil(Line, ref n, "]", out Max))
            {
                return null;
            }
            return new TSetting.TRange(Min, Max);
        }

        bool ParseValue(string Line, ref int n, out int Value)
        {
            return ParseIntUntil(Line, ref n, null, out Value);
        }

        bool CharArrayContains(char[] Array, char x)
        {
            foreach (var c in Array)
            {
                if (c == x)
                {
                    return true;
                }
            }
            return false;
        }

        string GetStringUntil(string Line, ref int n, char[] Tokens)
        {
            string Result = "";
            while (!CharArrayContains(Tokens, Line[n]))
            {
                Result += Line[n];
                n++;
            }
            return Result;
        }

        string[] ParseOptions(string Line, ref int n)
        {
            if (Line[n] != '{')
            {
                return null;
            }
            n++;
            List<string> Options = new List<string>();
            while (true)
            {
                string Temp = GetStringUntil(Line, ref n, new char[] { ',', '}' });
                if (Temp != null && Temp.Length > 0)
                {
                    Options.Add(Temp);
                }
                if (Line[n] == '}')
                {
                    break;
                }
                else
                {
                    n++;
                }
            }

            return Options.ToArray();
        }

        int[] CheckIfAllIntegers(string[] Options)
        {
            int[] Result = new int[Options.Length];

            for (int n = 0; n < Options.Length; n++)
            {
                if (!int.TryParse(Options[n], out Result[n]))
                {
                    return null;
                }
            }
            return Result;
        }

        float GetScaleFactor(string Name)
        {
            switch (Name)
            {
                case "SERIAL_SPEED":
                    return 0.001F;
            }
            return 1;
        }

        TSetting.TOption[] CreateOptions(TSetting.TRange Range, string[] Options,
            string Name)
        {
            if (Range == null || Options == null)
            {
                return null;
            }

            int[] Ints = CheckIfAllIntegers(Options);
            if (Ints != null)
            {
                TSetting.TOption[] Result = new TSetting.TOption[Ints.Length];
                float SF = GetScaleFactor(Name);
                for (int n = 0; n < Result.Length; n++)
                {
                    Result[n] = new TSetting.TOption((int)(SF * Ints[n]), Options[n]);
                }
                return Result;
            }

            //Not all integers.
            if (Range == null)
            {
                return null;
            }
            if (Options.Length == 2)
            {
                //Match up with min and max.
                TSetting.TOption[] Result = new TSetting.TOption[2];
                Result[0] = new TSetting.TOption(Range.Min, Options[0]);
                Result[1] = new TSetting.TOption(Range.Max, Options[1]);
                return Result;
            }
            else
            {
                return null;
            }
        }

        int GetIncrement(string Name)
        {
            switch (Name)
            {
                case "MIN_FREQ":
                case "MAX_FREQ":
                    return 500;
                case "MAX_WINDOW":
                    return 20;
                default:
                    return 1;
            }
        }

        /// <summary>
        /// A valid line looks like one of the following formats:
        /// Designator:Name:(Type)[Range]=Value{Options}\r\n
        ///     S2:AIR_SPEED(L)[4..1000]=125{4,64,125,250,500,1000,}<\r><\n>
        /// Designator:Name=Value\r\n
        ///     S2:AIR_SPEED=64<\r><\n>
        /// </summary>
        /// <param name="Line">The line.  Must not be null.</param>
        /// <returns>The setting parsed, or null if could not parse setting.</returns>
        TSetting ParseATI5QueryResponseLine(string Line)
        {
            try
            {
                int n = 0;
                string Designator = ParseDesignator(Line, ref n);
                if (Designator == null)
                {
                    return null;
                }

                n++;
                string Name = ParseName(Line, ref n);
                if (Name == null || Name.Length == 0)
                {
                    return null;
                }
                TSetting.TRange Range = null;
                if (Line[n] == '(')
                {
                    if (!ParseType(Line, ref n))
                    {
                        return null;
                    }
                    Range = ParseRange(Line, ref n);
                    if (Range == null)
                    {
                        return null;
                    }
                    
                }
                if (Line[n] != '=')
                {
                    return null;
                }
                n++;
                
                int Value;
                if (!ParseValue(Line, ref n, out Value))
                {
                    return null;
                }
                string[] Options = null;

                if (n < Line.Length)
                {
                    switch (Line[n])
                    {
                        case '\r':
                            break;
                        case '{':
                            Options = ParseOptions(Line, ref n);
                            break;
                        default:
                            return null;
                    }
                }

                return new TSetting(Designator, Name, Range, Value, 
                    CreateOptions(Range, Options, Name), GetIncrement(Name));
            }
            catch
            {
                return null;
            }
        }

        TSetting.TOption[] GetBaudRateOptionsGivenRawBaudRates(int[] Rates)
        {
            List<TSetting.TOption> Options = new List<TSetting.TOption>();
            foreach (var Rate in Rates)
            {
                Options.Add(new TSetting.TOption(Rate / 1000, Rate.ToString()));
            }
            return Options.ToArray();
        }

        TSetting.TOption[] GetDefaultBaudRateSettingForBoard(uploader.Uploader.Board Board)
        {
            switch (Board)
            {
                case uploader.Uploader.Board.DEVICE_ID_RFD900X:
                    return GetBaudRateOptionsGivenRawBaudRates(
                        new int[] { 1200, 2400, 4800, 9600, 19200, 38400, 57600, 115200, 230400, 460800 });
                default:
                    return GetBaudRateOptionsGivenRawBaudRates(
                        new int[] { 1200, 2400, 4800, 9600, 19200, 38400, 57600, 115200});
            }
        }

        /// <summary>
        /// Parse an ATI5 query response, into a list of settings.
        /// </summary>
        /// <param name="ATI5Response">The full ATI5 query response.  Must not be null.</param>
        /// <returns>A dictionary of settings found, with name as key.  Never null.</returns>
        Dictionary<string, TSetting> ParseATI5QueryResponse(string ATI5Response,
            Dictionary<string, TSetting> Dict)
        {
            foreach (var Line in ATI5Response.Split('\n', '\r'))
            {
                var S = ParseATI5QueryResponseLine(Line);
                if (S != null)
                {
                    Dict[S.Name] = S;
                }
            }

            return Dict;
        }

        public Dictionary<string, TSetting> GetSettings(string ATI5Response,
            uploader.Uploader.Board Board)
        {
            Dictionary<string, TSetting> Result = new Dictionary<string, TSetting>();
            ParseATI5QueryResponse(ATI5Response, Result);
            string SerialName = "SERIAL_SPEED";
            if (Result.ContainsKey(SerialName))
            {
                if (Result[SerialName].Options == null)
                {
                    Result[SerialName].Options = GetDefaultBaudRateSettingForBoard(Board);
                }
            }
            return Result;
        }
    }

    public class TSetting
    {
        public string Designator;
        public string Name;
        /// <summary>
        /// null if range unknown
        /// </summary>
        public TRange Range;
        public int Value;
        /// <summary>
        /// null if options unknown
        /// </summary>
        public TOption[] Options;
        public int Increment;

        public TSetting(string Designator, string Name, TRange Range, int Value, TOption[] Options,
            int Increment)
        {
            this.Designator = Designator;
            this.Name = Name;
            this.Range = Range;
            this.Value = Value;
            this.Options = Options;
            this.Increment = Increment;
        }

        public string[] GetOptionNames()
        {
            if (Options == null)
            {
                return null;
            }
            else
            {
                return RFDLib.Array.CherryPickArray(Options, (x) => x.OptionName);
            }
        }

        public string GetOptionNameForValue(string Value)
        {
            if (Options != null)
            {
                foreach (var O in Options)
                {
                    if (O.Value.ToString() == Value)
                    {
                        return O.OptionName;
                    }
                }
            }
            return null;
        }

        public class TRange
        {
            public readonly int Min;
            public readonly int Max;

            public TRange(int Min, int Max)
            {
                this.Min = Min;
                this.Max = Max;
            }
        }

        public class TOption
        {
            public readonly int Value;
            public readonly string OptionName;

            public TOption(int Value, string OptionName)
            {
                this.Value = Value;
                this.OptionName = OptionName;
            }
        }
    }

    public abstract class RFD900
    {
        protected TSession _Session;

        public RFD900(TSession Session)
        {
            _Session = Session;
        }

        public abstract bool ProgramFirmware(string FilePath, Action<double> Progress);

        static bool SearchTokenUpdate(byte[] Token, ref int TokenIndex, byte NextByte)
        {
            if (NextByte == Token[TokenIndex])
            {
                if (++TokenIndex >= Token.Length)
                {
                    //Found it.
                    return true;
                }
            }
            else
            {
                TokenIndex = 0;
            }
            return false;
        }

        /// <summary>
        /// Search the given binary file for the given token
        /// </summary>
        /// <param name="BinFilePath">The binary file path.  Must not be null.</param>
        /// <param name="Token">The token.  Must not be null.</param>
        /// <returns>true if found, false if not.</returns>
        protected static bool SearchBinary(string BinFilePath, string Token)
        {
            byte[] BinToken = System.Text.ASCIIEncoding.ASCII.GetBytes(Token);

            using (System.IO.FileStream FS = new System.IO.FileStream(BinFilePath, System.IO.FileMode.Open))
            {
                int Byte;
                int TokenIndex = 0;
                while ((Byte = FS.ReadByte()) != -1)
                {
                    if (SearchTokenUpdate(BinToken, ref TokenIndex, (byte)Byte))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Search the given hex file for the given token.
        /// </summary>
        /// <param name="File">The file to search.  Must not be null.</param>
        /// <param name="Token">The token to search for.  Must not be null.</param>
        /// <returns>true if found, false if not found.</returns>
        protected static bool SearchHex(uploader.IHex File, string Token)
        {
            int TokenIndex = 0;
            byte[] BinToken = System.Text.ASCIIEncoding.ASCII.GetBytes(Token);

            foreach (var Part in File.Values)
            {
                foreach (byte b in Part)
                {
                    if (SearchTokenUpdate(BinToken, ref TokenIndex, b))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        protected abstract string GetFirmwareSearchToken();

        protected void ShowWrongFirmwareMessageBox()
        {
            System.Windows.Forms.MessageBox.Show("File doesn't appear to be valid for this radio.  Could not find \"" +
                    GetFirmwareSearchToken() + "\" in file.");
        }
    }

    public abstract class RFD900APU : RFD900
    {
        public RFD900APU(TSession Session)
            : base(Session)
        {

        }

        public override bool ProgramFirmware(string FilePath, Action<double> Progress)
        {
            try
            {
                uploader.IHex Hex = new uploader.IHex();
                Hex.load(FilePath);

                if (SearchHex(Hex, GetFirmwareSearchToken()))
                {
                    uploader.Uploader UL = new uploader.Uploader();
                    UL.ProgressEvent += new MissionPlanner.Sikradio.ProgressEventHandler(Progress);
                    UL.upload(_Session.Port, Hex);
                    return true;
                }
                else
                {
                    ShowWrongFirmwareMessageBox();
                    return false;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Get the correct object for the modem which is in bootloader mode.
        /// </summary>
        /// <param name="Session">The session.  Must not be null.</param>
        /// <returns>null if could not generate modem object</returns>
        public static RFD900APU GetObjectForModem(TSession Session)
        {
            uploader.Uploader U = new uploader.Uploader();
            U.port = Session.Port;
            uploader.Uploader.Board Board = uploader.Uploader.Board.FAILED;
            uploader.Uploader.Frequency Freq = uploader.Uploader.Frequency.FAILED;

            U.getDevice(ref Board, ref Freq);
            switch (Board)
            {
                case uploader.Uploader.Board.DEVICE_ID_RFD900A:
                    return new RFD900a(Session);
                case uploader.Uploader.Board.DEVICE_ID_RFD900P:
                    return new RFD900p(Session);
                case uploader.Uploader.Board.DEVICE_ID_RFD900U:
                    return new RFD900u(Session);
                default:
                    return null;
            }
        }
    }

    public class RFD900a : RFD900APU
    {
        public RFD900a(TSession Session)
            : base(Session)
        {

        }

        protected override string GetFirmwareSearchToken()
        {
            return "RFD900A";
        }
    }

    public class RFD900p : RFD900APU
    {
        public RFD900p(TSession Session)
            : base(Session)
        {

        }

        protected override string GetFirmwareSearchToken()
        {
            return "RFD900P";
        }
    }

    public class RFD900u : RFD900APU
    {
        public RFD900u(TSession Session)
            : base(Session)
        {

        }

        protected override string GetFirmwareSearchToken()
        {
            return "RFD900U";
        }
    }

    public class RFD900x : RFD900
    {
        public RFD900x(TSession Session)
            : base(Session)
        {

        }

        public override bool ProgramFirmware(string FilePath, Action<double> Progress)
        {
            if (SearchBinary(FilePath, GetFirmwareSearchToken()))
            {
                _Session.Port.Write("\r");
                Thread.Sleep(100);
                _Session.Port.DiscardInBuffer();
                _Session.Port.Write("UPLOAD\r");
                if (_Session.WaitForToken("Ready\r\n", 5000) && _Session.WaitForToken("C", 5000))
                {
                    try
                    {
                        MissionPlanner.Radio.XModem.ProgressEvent += new MissionPlanner.Sikradio.ProgressEventHandler(Progress);
                        MissionPlanner.Radio.XModem.Upload(FilePath, _Session.Port);
                        return true;
                    }
                    catch
                    {
                        return false;
                    }
                }
                else
                {
                    return false;
                }
            }
            else
            {
                ShowWrongFirmwareMessageBox();
                return false;
            }
        }

        protected override string GetFirmwareSearchToken()
        {
            return "RFD900x";
        }
    }
}