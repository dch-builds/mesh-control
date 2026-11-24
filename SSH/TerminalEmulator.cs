using System.Text;

namespace MeshControl.Ssh;

/// <summary>Une cellule du terminal : un caractère et ses attributs de style.</summary>
public struct TerminalCell
{
    public char Char;
    public byte Foreground;   // index de couleur ANSI (0-15), 7 = gris clair par défaut
    public byte Background;   // 0 = fond par défaut, sinon index ANSI + 1
    public bool Bold;
    public bool Underline;
    public bool Inverse;

    public static TerminalCell Blank => new()
    {
        Char = ' ', Foreground = 7, Background = 0,
    };
}

/// <summary>
/// Émulateur de terminal ANSI/VT100 minimal mais suffisant pour l'administration
/// système : couleurs SGR, positionnement du curseur, effacements, défilement,
/// zones de défilement. Écrit à la main pour éviter une dépendance externe.
///
/// Il maintient une grille de cellules que la vue lit pour dessiner ; il ne
/// s'occupe ni du rendu ni des polices.
/// </summary>
public sealed class TerminalEmulator
{
    // Caractères de contrôle, nommés pour éviter tout littéral invisible.
    private const char Esc = '\u001B';
    private const char Bel = '\u0007';

    public int Cols { get; private set; }
    public int Rows { get; private set; }
    public int CursorCol { get; private set; }
    public int CursorRow { get; private set; }
    public bool CursorVisible { get; private set; } = true;

    /// <summary>
    /// Collage protégé (mode 2004) demandé par le shell distant : un texte
    /// collé doit alors lui parvenir encadré, pour qu'il ne l'exécute pas.
    /// </summary>
    public bool BracketedPaste { get; private set; }

    /// <summary>Grille courante : [ligne][colonne].</summary>
    public TerminalCell[][] Grid { get; private set; } = [];

    /// <summary>
    /// Réponse que le terminal doit renvoyer au programme distant, comme s'il
    /// l'avait tapée : position du curseur, identification du terminal.
    ///
    /// Indispensable sous Windows : la console (ConPTY) demande la position du
    /// curseur à l'ouverture et peut attendre la réponse avant d'afficher quoi
    /// que ce soit. Sans elle, le terminal restait noir sur certains postes.
    /// </summary>
    public event Action<string>? Reply;

    /// <summary>Incrémenté à chaque modification, pour déclencher un redessin.</summary>
    public int Revision { get; private set; }

    /// <summary>
    /// Nombre de lignes conservées après leur sortie par le haut de l'écran.
    /// Au-delà, les plus anciennes sont oubliées : une sortie continue — un
    /// suivi de journal, par exemple — finirait sinon par occuper toute la
    /// mémoire disponible.
    /// </summary>
    public const int ScrollbackLimit = 5000;

    // Tampon circulaire : les lignes entrent une à une et ne ressortent que par
    // l'autre bout. Une liste obligerait à décaler tout le contenu à chaque
    // ligne une fois la limite atteinte, soit à chaque ligne affichée.
    private readonly TerminalCell[]?[] _scrollback = new TerminalCell[ScrollbackLimit][];
    private int _scrollbackStart;

    /// <summary>Lignes d'historique actuellement conservées.</summary>
    public int ScrollbackCount { get; private set; }

    // Style courant appliqué aux caractères écrits.
    private TerminalCell _pen = TerminalCell.Blank;

    // Zone de défilement (indices de ligne inclusifs).
    private int _scrollTop;
    private int _scrollBottom;

    // Analyseur de séquences d'échappement.
    private enum ParseState { Ground, Escape, Csi, Osc, OscEscape, CharsetIgnore }
    private ParseState _state = ParseState.Ground;
    private readonly StringBuilder _paramBuffer = new();
    private readonly StringBuilder _oscBuffer = new();

    // Curseur sauvegardé (ESC 7 / ESC 8, CSI s / CSI u).
    private int _savedCol, _savedRow;

    // Bornes défensives : le flux vient d'une machine qui peut être hostile.
    // Paramètres CSI et titres OSC n'approchent jamais ces longueurs, et un
    // compteur (lignes à insérer, caractères à effacer) ne dépasse jamais
    // l'écran — au-delà, ESC[999999999L gelait l'interface entière.
    private const int MaxParamLength = 256;
    private const int MaxOscLength = 4096;
    private const int MaxCount = 10_000;

    /// <summary>
    /// Curseur sauvegardé, ramené dans la grille : elle a pu rétrécir depuis
    /// la sauvegarde, et l'écriture suivante serait sortie de ses bornes.
    /// </summary>
    private void RestoreCursor()
    {
        CursorCol = Math.Clamp(_savedCol, 0, Cols - 1);
        CursorRow = Math.Clamp(_savedRow, 0, Rows - 1);
    }

    public TerminalEmulator(int cols = 80, int rows = 24)
    {
        Cols = 0; Rows = 0;
        Resize(cols, rows);
    }

    public void Resize(int cols, int rows)
    {
        cols = Math.Max(2, cols);
        rows = Math.Max(2, rows);
        if (cols == Cols && rows == Rows) return;

        var grid = new TerminalCell[rows][];
        for (int r = 0; r < rows; r++)
        {
            grid[r] = new TerminalCell[cols];
            for (int c = 0; c < cols; c++)
            {
                // Conserver le contenu existant lorsque c'est possible.
                grid[r][c] = (r < Rows && c < Cols && Grid.Length > r && Grid[r].Length > c)
                    ? Grid[r][c]
                    : TerminalCell.Blank;
            }
        }

        Grid = grid;
        Cols = cols;
        Rows = rows;
        _scrollTop = 0;
        _scrollBottom = rows - 1;
        CursorCol = Math.Min(CursorCol, cols - 1);
        CursorRow = Math.Min(CursorRow, rows - 1);
        Revision++;
    }

    /// <summary>Traite un flux de texte reçu du shell distant.</summary>
    public void Write(string text)
    {
        foreach (var ch in text) Consume(ch);
        Revision++;
    }

    private void Consume(char ch)
    {
        switch (_state)
        {
            case ParseState.Ground:
                ConsumeGround(ch);
                break;

            case ParseState.Escape:
                switch (ch)
                {
                    case '[': _state = ParseState.Csi; _paramBuffer.Clear(); break;
                    case ']': _state = ParseState.Osc; _oscBuffer.Clear(); break;
                    case '7': _savedCol = CursorCol; _savedRow = CursorRow; _state = ParseState.Ground; break;
                    case '8': RestoreCursor(); _state = ParseState.Ground; break;
                    case 'M': ReverseIndex(); _state = ParseState.Ground; break;
                    case 'c': HardReset(); _state = ParseState.Ground; break;
                    // Sélection de jeu de caractères (ESC ( B, ESC ) 0 …) :
                    // l'octet suivant doit être avalé, sinon il s'afficherait.
                    case '(' or ')' or '*' or '+': _state = ParseState.CharsetIgnore; break;
                    default: _state = ParseState.Ground; break;
                }
                break;

            case ParseState.Csi:
                // Paramètres : chiffres, ';', '?', '>' … puis une lettre finale.
                if ((ch >= '0' && ch <= '9') || ch is ';' or '?' or '>' or '=' or ' ')
                {
                    // Séquence démesurée : aucune séquence légitime n'en approche,
                    // on l'abandonne plutôt que de laisser grossir le tampon.
                    if (_paramBuffer.Length >= MaxParamLength) _state = ParseState.Ground;
                    else _paramBuffer.Append(ch);
                }
                else
                {
                    ExecuteCsi(ch, _paramBuffer.ToString());
                    _state = ParseState.Ground;
                }
                break;

            case ParseState.Osc:
                // Titre de fenêtre (ESC ] 0 ; texte BEL) : terminé par BEL, ou par
                // la séquence ST « ESC \ ».
                // Attention : un antislash SEUL ne termine PAS l'OSC — les titres
                // Windows en contiennent (« WINDOWS\System32\cmd.exe »), et sortir
                // au premier « \ » faisait écrire la fin du titre dans la grille.
                if (ch == Bel)
                {
                    _state = ParseState.Ground;
                }
                else if (ch == Esc)
                {
                    _state = ParseState.OscEscape;   // attendre le « \ » du ST
                }
                else if (_oscBuffer.Length < MaxOscLength)
                {
                    // Au-delà, le texte est ignoré jusqu'à la fin de la séquence.
                    _oscBuffer.Append(ch);
                }
                break;

            case ParseState.CharsetIgnore:
                // Octet de désignation du jeu de caractères : absorbé sans effet.
                _state = ParseState.Ground;
                break;

            case ParseState.OscEscape:
                // On vient de lire ESC dans un OSC : « \ » clôt la séquence, tout
                // autre caractère signifie que c'était le début d'une nouvelle
                // séquence d'échappement.
                if (ch == '\\')
                {
                    _state = ParseState.Ground;
                }
                else
                {
                    _state = ParseState.Escape;
                    Consume(ch);
                }
                break;
        }
    }

    private void ConsumeGround(char ch)
    {
        switch (ch)
        {
            case Esc: _state = ParseState.Escape; break;
            case '\r': CursorCol = 0; break;
            case '\n': LineFeed(); break;
            case '\b': if (CursorCol > 0) CursorCol--; break;
            case '\t':
                // Tabulation tous les 8 caractères.
                CursorCol = Math.Min(Cols - 1, (CursorCol / 8 + 1) * 8);
                break;
            case Bel: break;   // signal sonore : ignoré
            default:
                if (!char.IsControl(ch)) PutChar(ch);
                break;
        }
    }

    private void PutChar(char ch)
    {
        if (CursorCol >= Cols)
        {
            CursorCol = 0;
            LineFeed();
        }
        var cell = _pen;
        cell.Char = ch;
        Grid[CursorRow][CursorCol] = cell;
        CursorCol++;
    }

    private void LineFeed()
    {
        if (CursorRow == _scrollBottom) ScrollUp();
        else if (CursorRow < Rows - 1) CursorRow++;
    }

    private void ReverseIndex()
    {
        if (CursorRow == _scrollTop) ScrollDown();
        else if (CursorRow > 0) CursorRow--;
    }

    private void ScrollUp()
    {
        // La ligne qui disparaît rejoint l'historique, mais seulement quand le
        // défilement porte sur l'écran entier. Une zone de défilement réduite
        // (CSI r) sert à animer une portion de l'affichage — un bandeau, une
        // barre de progression — et ses lignes ne sont pas de la sortie passée.
        if (_scrollTop == 0 && _scrollBottom == Rows - 1) PushScrollback(Grid[0]);

        for (int r = _scrollTop; r < _scrollBottom; r++) Grid[r] = Grid[r + 1];
        Grid[_scrollBottom] = NewBlankLine();
    }

    private void PushScrollback(TerminalCell[] line)
    {
        _scrollback[(_scrollbackStart + ScrollbackCount) % ScrollbackLimit] = line;

        if (ScrollbackCount < ScrollbackLimit) ScrollbackCount++;
        else _scrollbackStart = (_scrollbackStart + 1) % ScrollbackLimit;
    }

    /// <summary>
    /// Une ligne par son numéro absolu : 0 à Rows-1 pour l'écran courant,
    /// négatif pour l'historique (-1 est la dernière ligne sortie par le haut).
    /// Renvoie une ligne vide hors de ces bornes, pour que la vue n'ait pas à
    /// vérifier ses propres calculs.
    ///
    /// Les lignes d'historique gardent la largeur qu'avait l'écran au moment où
    /// elles sont sorties : elles peuvent donc être plus courtes ou plus longues
    /// que <see cref="Cols"/>.
    /// </summary>
    public TerminalCell[] LineAt(int row)
    {
        if (row >= 0) return row < Rows ? Grid[row] : [];

        var index = ScrollbackCount + row;
        if (index < 0) return [];
        return _scrollback[(_scrollbackStart + index) % ScrollbackLimit] ?? [];
    }

    /// <summary>Oublie l'historique, sans toucher à l'écran courant.</summary>
    public void ClearScrollback()
    {
        Array.Clear(_scrollback);
        _scrollbackStart = 0;
        ScrollbackCount = 0;
        Revision++;
    }

    private void ScrollDown()
    {
        for (int r = _scrollBottom; r > _scrollTop; r--) Grid[r] = Grid[r - 1];
        Grid[_scrollTop] = NewBlankLine();
    }

    private TerminalCell[] NewBlankLine()
    {
        var line = new TerminalCell[Cols];
        for (int c = 0; c < Cols; c++)
        {
            line[c] = TerminalCell.Blank;
            // Le fond courant est conservé, comme le veut la spécification.
            line[c].Background = _pen.Background;
        }
        return line;
    }

    private void ExecuteCsi(char final, string paramText)
    {
        // Les séquences privées (CSI ? …) ne concernent ici que le mode curseur.
        bool priv = paramText.StartsWith('?');
        var clean = priv ? paramText[1..] : paramText;
        var args = clean.Split(';', StringSplitOptions.TrimEntries);

        int Arg(int index, int fallback = 1)
        {
            if (index >= args.Length) return fallback;
            return int.TryParse(args[index], out var v) && v > 0 ? Math.Min(v, MaxCount) : fallback;
        }
        int Arg0(int index) =>
            index < args.Length && int.TryParse(args[index], out var v) ? Math.Clamp(v, 0, MaxCount) : 0;

        switch (final)
        {
            case 'A': CursorRow = Math.Max(0, CursorRow - Arg(0)); break;
            case 'B': CursorRow = Math.Min(Rows - 1, CursorRow + Arg(0)); break;
            case 'C': CursorCol = Math.Min(Cols - 1, CursorCol + Arg(0)); break;
            case 'D': CursorCol = Math.Max(0, CursorCol - Arg(0)); break;
            case 'E': CursorRow = Math.Min(Rows - 1, CursorRow + Arg(0)); CursorCol = 0; break;
            case 'F': CursorRow = Math.Max(0, CursorRow - Arg(0)); CursorCol = 0; break;
            case 'G': CursorCol = Math.Clamp(Arg(0) - 1, 0, Cols - 1); break;

            case 'H':
            case 'f':
                CursorRow = Math.Clamp(Arg(0) - 1, 0, Rows - 1);
                CursorCol = Math.Clamp(Arg(1) - 1, 0, Cols - 1);
                break;

            case 'J': EraseInDisplay(Arg0(0)); break;
            case 'K': EraseInLine(Arg0(0)); break;

            case 'L': InsertLines(Arg(0)); break;
            case 'M': DeleteLines(Arg(0)); break;
            case 'P': DeleteChars(Arg(0)); break;
            case '@': InsertChars(Arg(0)); break;
            case 'X': EraseChars(Arg(0)); break;

            case 'd': CursorRow = Math.Clamp(Arg(0) - 1, 0, Rows - 1); break;

            case 'r':
                _scrollTop = Math.Clamp(Arg(0) - 1, 0, Rows - 1);
                _scrollBottom = Math.Clamp(Arg(1, Rows) - 1, _scrollTop, Rows - 1);
                break;

            case 'm': ApplySgr(args); break;

            case 'h': SetPrivateModes(priv, args, true); break;
            case 'l': SetPrivateModes(priv, args, false); break;

            case 's': _savedCol = CursorCol; _savedRow = CursorRow; break;
            case 'u': RestoreCursor(); break;

            // Rapports d'état (DSR) : 5 = état du terminal, 6 = position du curseur.
            case 'n':
                if (Arg0(0) == 5 && !priv)
                    Reply?.Invoke($"{Esc}[0n");
                else if (Arg0(0) == 6)
                    Reply?.Invoke(priv
                        ? $"{Esc}[?{CursorRow + 1};{CursorCol + 1}R"
                        : $"{Esc}[{CursorRow + 1};{CursorCol + 1}R");
                break;

            // Identification (DA) : on se présente comme un VT100 avec options
            // avancées, ce que reconnaissent tous les programmes courants.
            case 'c':
                if (paramText.StartsWith('>'))
                    Reply?.Invoke($"{Esc}[>0;10;1c");
                else if (!priv && Arg0(0) == 0)
                    Reply?.Invoke($"{Esc}[?1;2c");
                break;
        }
    }

    /// <summary>Modes privés (CSI ? … h / l) que l'émulateur prend en compte.</summary>
    private void SetPrivateModes(bool priv, string[] modes, bool on)
    {
        if (!priv) return;
        foreach (var mode in modes)
        {
            switch (mode)
            {
                case "25": CursorVisible = on; break;
                case "2004": BracketedPaste = on; break;
            }
        }
    }

    private void ApplySgr(string[] args)
    {
        if (args.Length == 0 || (args.Length == 1 && args[0].Length == 0))
        {
            _pen = TerminalCell.Blank;
            return;
        }

        for (int i = 0; i < args.Length; i++)
        {
            if (!int.TryParse(args[i], out var code)) continue;
            switch (code)
            {
                case 0: _pen = TerminalCell.Blank; break;
                case 1: _pen.Bold = true; break;
                case 4: _pen.Underline = true; break;
                case 7: _pen.Inverse = true; break;
                case 22: _pen.Bold = false; break;
                case 24: _pen.Underline = false; break;
                case 27: _pen.Inverse = false; break;
                case 39: _pen.Foreground = 7; break;
                case 49: _pen.Background = 0; break;

                case >= 30 and <= 37: _pen.Foreground = (byte)(code - 30); break;
                case >= 90 and <= 97: _pen.Foreground = (byte)(code - 90 + 8); break;
                case >= 40 and <= 47: _pen.Background = (byte)(code - 40 + 1); break;
                case >= 100 and <= 107: _pen.Background = (byte)(code - 100 + 9); break;

                // Couleurs étendues : 38;5;n (256 couleurs) ou 38;2;r;g;b (RGB).
                // On les ramène aux 16 couleurs de base : suffisant pour un
                // terminal d'administration, et cohérent avec le rendu de la vue.
                case 38 or 48:
                {
                    bool isForeground = code == 38;
                    if (i + 2 < args.Length && args[i + 1] == "5"
                        && int.TryParse(args[i + 2], out var idx))
                    {
                        var mapped = (byte)(idx < 16 ? idx : 7);
                        if (isForeground) _pen.Foreground = mapped;
                        else _pen.Background = (byte)(mapped + 1);
                        i += 2;
                    }
                    else if (i + 4 < args.Length && args[i + 1] == "2")
                    {
                        if (isForeground) _pen.Foreground = 7; else _pen.Background = 0;
                        i += 4;
                    }
                    break;
                }
            }
        }
    }

    private void EraseInDisplay(int mode)
    {
        switch (mode)
        {
            case 0:   // du curseur à la fin de l'écran
                EraseInLine(0);
                for (int r = CursorRow + 1; r < Rows; r++) Grid[r] = NewBlankLine();
                break;
            case 1:   // du début de l'écran au curseur
                for (int r = 0; r < CursorRow; r++) Grid[r] = NewBlankLine();
                EraseInLine(1);
                break;
            case 3:   // l'historique seul — « clear » l'envoie après le mode 2
                ClearScrollback();
                break;
            default:  // tout l'écran
                for (int r = 0; r < Rows; r++) Grid[r] = NewBlankLine();
                break;
        }
    }

    private void EraseInLine(int mode)
    {
        var line = Grid[CursorRow];
        int from = mode == 1 ? 0 : (mode == 0 ? CursorCol : 0);
        int to = mode == 1 ? CursorCol : Cols - 1;
        for (int c = from; c <= to && c < Cols; c++)
        {
            line[c] = TerminalCell.Blank;
            line[c].Background = _pen.Background;
        }
    }

    private void InsertLines(int count)
    {
        count = Math.Min(count, _scrollBottom - CursorRow + 1);
        for (int n = 0; n < count; n++)
        {
            for (int r = _scrollBottom; r > CursorRow; r--) Grid[r] = Grid[r - 1];
            Grid[CursorRow] = NewBlankLine();
        }
    }

    private void DeleteLines(int count)
    {
        count = Math.Min(count, _scrollBottom - CursorRow + 1);
        for (int n = 0; n < count; n++)
        {
            for (int r = CursorRow; r < _scrollBottom; r++) Grid[r] = Grid[r + 1];
            Grid[_scrollBottom] = NewBlankLine();
        }
    }

    private void DeleteChars(int count)
    {
        var line = Grid[CursorRow];
        for (int c = CursorCol; c < Cols; c++)
            line[c] = c + count < Cols ? line[c + count] : TerminalCell.Blank;
    }

    private void InsertChars(int count)
    {
        var line = Grid[CursorRow];
        for (int c = Cols - 1; c >= CursorCol; c--)
            line[c] = c - count >= CursorCol ? line[c - count] : TerminalCell.Blank;
    }

    private void EraseChars(int count)
    {
        var line = Grid[CursorRow];
        for (int c = CursorCol; c < Math.Min(Cols, CursorCol + count); c++)
            line[c] = TerminalCell.Blank;
    }

    private void HardReset()
    {
        _pen = TerminalCell.Blank;
        CursorCol = CursorRow = 0;
        _scrollTop = 0;
        _scrollBottom = Rows - 1;
        CursorVisible = true;
        BracketedPaste = false;
        for (int r = 0; r < Rows; r++) Grid[r] = NewBlankLine();
        ClearScrollback();
    }

    /// <summary>
    /// Contenu textuel, pour le presse-papiers. L'historique est inclus par
    /// défaut : « Copier tout » doit rendre toute la sortie, pas seulement la
    /// hauteur de la fenêtre.
    /// </summary>
    public string GetText(bool includeScrollback = true)
    {
        var sb = new StringBuilder();
        var first = includeScrollback ? -ScrollbackCount : 0;

        for (int row = first; row < Rows; row++)
            sb.AppendLine(LineText(LineAt(row)));

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Texte d'une ligne, espaces de fin retirés : la grille est toujours pleine
    /// de blancs jusqu'au bord droit, qu'on ne veut pas dans le presse-papiers.
    /// </summary>
    public static string LineText(TerminalCell[] line) =>
        new string(line.Select(c => c.Char).ToArray()).TrimEnd();
}
