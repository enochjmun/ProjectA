using UnityEngine;

/// <summary>
/// Shows one card face from the playing-card ATLAS by index, by setting the front material's UV tiling +
/// offset via a MaterialPropertyBlock (so every card shares ONE material and you don't make 54 materials).
///
/// Atlas layout (PlayingCards_Deck.png): 13 columns x 5 rows.
///   index = suit*13 + rank   (suits C,D,H,S = 0..3 ; ranks A,2..10,J,Q,K = 0..12)  -> cards 0..51
///   52 = red joker, 53 = black joker, 54 = card back
///   col = index % 13, row = index / 13 ; Tiling = (1/13, 1/5), Offset = (col/13, row/5)
///
/// Works in the editor ([ExecuteAlways] + OnValidate) so you can pick a face in the Inspector for the menu
/// vignette; at runtime OldMaidCardView calls SetCard(index) when it deals. Drives ONLY the front material
/// slot -- the back face uses its own fixed back material.
///
/// SETUP: put on the card. Assign `targetRenderer` (the card mesh renderer) and `frontMaterialIndex` (the
/// slot the card-face material is on, usually 0). Set `cardIndex` to preview a face.
/// </summary>
[ExecuteAlways]
public class CardFace : MonoBehaviour
{
    public const int JokerRed = 52, JokerBlack = 53, Back = 54;

    [SerializeField] private Renderer targetRenderer;
    [Tooltip("Material slot the card-FACE (atlas) material is on. The card back is a different slot.")]
    [SerializeField] private int frontMaterialIndex = 0;
    [Range(0, 54)] [SerializeField] private int cardIndex = 0;

    [Header("Atlas grid")]
    [SerializeField] private int columns = 13;
    [SerializeField] private int rows = 5;

    private static readonly int STId = Shader.PropertyToID("_BaseMap_ST");
    private MaterialPropertyBlock _mpb;

    /// <summary>suit 0-3 (C,D,H,S), rank 0-12 (A..K).</summary>
    public void SetCard(int suit, int rank) => SetCard(suit * 13 + rank);

    public void SetCard(int index)
    {
        cardIndex = Mathf.Clamp(index, 0, columns * rows - 1);
        Apply();
    }

    private void OnEnable()   => Apply();
    private void OnValidate() => Apply();

    private void Apply()
    {
        if (targetRenderer == null) targetRenderer = GetComponent<Renderer>();
        if (targetRenderer == null) return;

        int col = cardIndex % columns;
        int row = cardIndex / columns;
        float tx = 1f / columns, ty = 1f / rows;
        // _BaseMap_ST = (tilingX, tilingY, offsetX, offsetY)
        var st = new Vector4(tx, ty, col * tx, row * ty);

        _mpb ??= new MaterialPropertyBlock();
        targetRenderer.GetPropertyBlock(_mpb, frontMaterialIndex);
        _mpb.SetVector(STId, st);
        targetRenderer.SetPropertyBlock(_mpb, frontMaterialIndex);
    }
}
