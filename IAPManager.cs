using UnityEngine;
using UnityEngine.Purchasing;
using System.Collections.Generic;

// Handles the 3 real-money carrot purchases, written against Unity IAP
// v5's new API (StoreController/UnityIAPServices — NOT the old
// IStoreListener/ProcessPurchase pattern from IAP v4 and earlier, which
// this project's installed version, 5.4.2, has fully replaced).
//
// Wire your 3 shop buttons to call BuyCarrots1000() / BuyCarrots5000() /
// BuyCarrots15000() directly — these product IDs must EXACTLY match what
// you set as the uSKU on the Unity Dashboard Catalog.
public class IAPManager : MonoBehaviour
{
    public static IAPManager Instance { get; private set; }

    public const string Product1000 = "carrots_1000";
    public const string Product5000 = "carrots_5000";
    public const string Product15000 = "carrots_15000";

    // How many carrots each product grants. Kept here (not on the store
    // side) since Unity IAP only knows about price/ID, not what the
    // product actually gives the player in-game.
    private static readonly Dictionary<string, long> carrotAmounts = new Dictionary<string, long>
    {
        { Product1000, 2000 },
        { Product5000, 10000 },
        { Product15000, 30000 },
    };

    private StoreController storeController;
    private bool isInitialized = false;

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    async void Start()
    {
        Debug.Log("[IAP DEBUG] Start() — creating StoreController");
        storeController = UnityIAPServices.StoreController();

        // Attached BEFORE Connect(), per Unity's v5 migration guide —
        // this version appears to require every relevant callback
        // registered upfront, or it silently blocks the corresponding
        // action (Purchase blocked without OnPurchaseDeferred, and now
        // apparently Connect complains without these two as well).
        storeController.OnPurchasePending += OnPurchasePending;
        storeController.OnPurchaseFailed += OnPurchaseFailed;
        storeController.OnPurchaseDeferred += OnPurchaseDeferred;
        storeController.OnStoreConnected += OnStoreConnected;
        storeController.OnStoreDisconnected += OnStoreDisconnected;
        storeController.OnPurchaseConfirmed += OnPurchaseConfirmed;

        Debug.Log("[IAP DEBUG] Connecting to store...");
        await storeController.Connect();
        Debug.Log("[IAP DEBUG] Connected.");

        storeController.OnProductsFetched += OnProductsFetched;
        storeController.OnProductsFetchFailed += OnProductsFetchFailed;
        storeController.OnPurchasesFetched += OnPurchasesFetched;
        storeController.OnPurchasesFetchFailed += OnPurchasesFetchFailed;

        var productsToFetch = new List<ProductDefinition>
        {
            new ProductDefinition(Product1000, ProductType.Consumable),
            new ProductDefinition(Product5000, ProductType.Consumable),
            new ProductDefinition(Product15000, ProductType.Consumable),
        };
        Debug.Log("[IAP DEBUG] Fetching products...");
        storeController.FetchProducts(productsToFetch);
    }

    void OnStoreConnected()
    {
        Debug.Log("[IAP DEBUG] OnStoreConnected fired.");
    }

    void OnStoreDisconnected(StoreConnectionFailureDescription description)
    {
        Debug.LogWarning($"[IAP DEBUG] OnStoreDisconnected fired — {description}");
    }

    void OnProductsFetched(List<Product> products)
    {
        isInitialized = true;
        Debug.Log($"[IAP DEBUG] OnProductsFetched — {products.Count} product(s): {string.Join(", ", products.ConvertAll(p => p.definition.id))}");

        // Matches Unity's own v5 example: FetchPurchases() should be
        // called right after products are fetched — this appears to be
        // part of what actually arms the purchase-pending pipeline,
        // rather than just being for restoring old purchases.
        Debug.Log("[IAP DEBUG] Calling FetchPurchases()...");
        storeController.FetchPurchases();
    }

    void OnPurchasesFetched(Orders orders)
    {
        Debug.Log("[IAP DEBUG] OnPurchasesFetched fired.");
    }

    void OnPurchasesFetchFailed(PurchasesFetchFailureDescription failure)
    {
        Debug.LogWarning($"[IAP DEBUG] OnPurchasesFetchFailed — {failure}");
    }

    void OnProductsFetchFailed(ProductFetchFailed failure)
    {
        Debug.LogError($"[IAP DEBUG] OnProductsFetchFailed — {failure}");
    }

    public bool IsReady() => isInitialized;

    // Wire these directly to your 3 buy buttons' OnClick().
    public void BuyCarrots1000() => BuyProduct(Product1000);
    public void BuyCarrots5000() => BuyProduct(Product5000);
    public void BuyCarrots15000() => BuyProduct(Product15000);

    void BuyProduct(string productId)
    {
        Debug.Log($"[IAP DEBUG] BuyProduct('{productId}') called. isInitialized={isInitialized}");

        if (!NetworkStatusManager.RequireWifiOrShowMessage())
        {
            Debug.Log("[IAP DEBUG] Blocked — no Wi-Fi.");
            return;
        }

        if (!isInitialized)
        {
            Debug.LogWarning("[IAP DEBUG] Not initialized yet — products may still be fetching.");
            return;
        }

        Debug.Log($"[IAP DEBUG] Calling storeController.PurchaseProduct('{productId}')...");
        storeController.PurchaseProduct(productId);
        Debug.Log("[IAP DEBUG] PurchaseProduct call returned (this just means the call was MADE, not that it succeeded — watch for OnPurchasePending/OnPurchaseFailed next).");
    }

    // Called for every new purchase (fake-store test purchases included).
    // NOTE: order.CartOrdered.Items() is the documented v5 pattern for
    // reading which product(s) were purchased — if your exact installed
    // sub-version renamed this, Unity's compiler error will point at the
    // exact correct name; send it my way and I'll adjust this one line.
    void OnPurchasePending(PendingOrder order)
    {
        Debug.Log("[IAP DEBUG] OnPurchasePending fired!");

        foreach (CartItem item in order.CartOrdered.Items())
        {
            string productId = item.Product.definition.id;
            Debug.Log($"[IAP DEBUG] Cart item: {productId}");

            if (carrotAmounts.TryGetValue(productId, out long amount))
            {
                GameManager.Instance?.AddCarrots(amount);
                AudioManager.Instance?.PlayBoostBought(); // reuse existing purchase sound for now
                Debug.Log($"[IAP DEBUG] Granted {amount} carrots for '{productId}'.");
            }
            else
            {
                Debug.LogWarning($"[IAP DEBUG] Unknown product '{productId}' — no carrots granted.");
            }
        }

        // Confirms the purchase with the store — REQUIRED, without this
        // the transaction stays pending forever and the store considers
        // it unresolved.
        Debug.Log("[IAP DEBUG] Calling ConfirmPurchase...");
        storeController.ConfirmPurchase(order);
        Debug.Log("[IAP DEBUG] ConfirmPurchase call returned.");
    }

    void OnPurchaseFailed(FailedOrder order)
    {
        Debug.LogWarning($"[IAP DEBUG] OnPurchaseFailed fired — {order.FailureReason}: {order.Details}");
    }

    // Fires for purchases that need external approval before they can
    // complete — e.g. a parent approving an "Ask to Buy" request, or a
    // pending payment method. Rare on the Fake Store, but v5 requires
    // this handler to exist before Purchase()/PurchaseProduct() will
    // proceed at all, even for purchases that never actually defer.
    void OnPurchaseDeferred(DeferredOrder order)
    {
        Debug.Log("[IAP DEBUG] OnPurchaseDeferred fired — awaiting external approval.");
    }

    void OnPurchaseConfirmed(Order order)
    {
        Debug.Log("[IAP DEBUG] OnPurchaseConfirmed fired — transaction fully finalized.");
    }
}
