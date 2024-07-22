defmodule OctoconWeb.Router do
  use OctoconWeb, :router

  import Phoenix.LiveDashboard.Router

  alias OctoconWeb.AuthPipeline

  pipeline :browser do
    plug :accepts, ["html"]
    plug :fetch_session
    plug :fetch_flash
    plug :protect_from_forgery
    plug :put_secure_browser_headers
  end

  pipeline :api do
    plug :accepts, ["json"]

    plug Hammer.Plug, [
      # Allow 10 requests per second
      rate_limit: {"api_requests", :timer.seconds(1), 10},
      by: :ip
    ]
  end

  pipeline :admins_only do
    plug :admin_basic_auth
  end

  pipeline :ensure_authenticated do
    plug Guardian.Plug.EnsureAuthenticated
  end

  # Requires authentication
  scope "/api", OctoconWeb do
    pipe_through [:api, AuthPipeline, :ensure_authenticated]

    scope "/systems/me" do
      scope "/alters" do
        post "/", System.AlterController, :create

        scope "/journals/:journal_id" do
          get "/", AlterJournalController, :show
          patch "/", AlterJournalController, :update
          delete "/", AlterJournalController, :delete
        end

        scope "/:id" do
          patch "/", System.AlterController, :update
          delete "/", System.AlterController, :delete

          put "/avatar", System.AlterController, :upload_avatar
          delete "/avatar", System.AlterController, :delete_avatar

          scope "/journals" do
            get "/", AlterJournalController, :index
            post "/", AlterJournalController, :create
          end
        end
      end

      scope "/tags" do
        get "/", System.TagController, :index
        post "/", System.TagController, :create

        scope "/:id" do
          get "/", System.TagController, :show
          patch "/", System.TagController, :update
          delete "/", System.TagController, :delete

          scope "/alter" do
            post "/", System.TagController, :attach_alter
            delete "/", System.TagController, :detach_alter
          end

          scope "/parent" do
            post "/", System.TagController, :set_parent
            delete "/", System.TagController, :remove_parent
          end
        end
      end

      scope "/front" do
        post "/", System.FrontingController, :update
        post "/start", System.FrontingController, :start
        post "/end", System.FrontingController, :endd
        post "/set", System.FrontingController, :set
        post "/primary", System.FrontingController, :primary

        get "/month", System.FrontingController, :month
        get "/between", System.FrontingController, :between

        scope "/:id" do
          get "/", System.FrontingController, :show
          delete "/", System.FrontingController, :delete
          post "/comment", System.FrontingController, :update_comment
        end
      end
    end

    scope "/friends" do
      get "/", FriendController, :index

      scope "/:id" do
        get "/", FriendController, :show
        delete "/", FriendController, :delete

        post "/trust", FriendController, :trust
        post "/untrust", FriendController, :untrust
      end
    end

    scope "/friend-requests" do
      get "/", FriendRequestController, :index

      scope "/:id" do
        put "/", FriendRequestController, :send
        delete "/", FriendRequestController, :cancel

        post "/accept", FriendRequestController, :accept
        post "/reject", FriendRequestController, :reject
      end
    end

    scope "/journals" do
      get "/", GlobalJournalController, :index
      post "/", GlobalJournalController, :create

      scope "/:id" do
        get "/", GlobalJournalController, :show
        patch "/", GlobalJournalController, :update
        delete "/", GlobalJournalController, :delete

        scope "/alter" do
          post "/", GlobalJournalController, :attach_alter
          delete "/", GlobalJournalController, :detach_alter
        end
      end
    end

    scope "/settings" do
      get "/link_token", AuthLinkTokenController, :get

      post "/username", SettingsController, :update_username
      put "/avatar", SettingsController, :upload_avatar
      delete "/avatar", SettingsController, :delete_avatar

      post "/push-token", SettingsController, :add_push_token
      delete "/push-token", SettingsController, :invalidate_push_token

      post "/import-pk", SettingsController, :import_pk
      post "/import-sp", SettingsController, :import_sp

      post "/unlink_discord", SettingsController, :unlink_discord
      post "/unlink_email", SettingsController, :unlink_email

      post "/description", SettingsController, :update_description

      scope "/fields" do
        scope "/:id" do
          patch "/", SettingsController, :edit_custom_field
          delete "/", SettingsController, :remove_custom_field
          post "/relocate", SettingsController, :relocate_custom_field
        end

        post "/", SettingsController, :create_custom_field
      end
    end
  end

  # Does not require authentication, but still has access to the Guardian resource
  scope "/api", OctoconWeb do
    pipe_through [:api, AuthPipeline]

    get "/heartbeat", HeartbeatController, :index

    scope "/systems/:system_id" do
      get "/", SystemController, :show

      scope "/alters" do
        get "/", System.AlterController, :index
        get "/:id", System.AlterController, :show
      end

      scope "/tags" do
        get "/", System.TagController, :index
        get "/:id", System.TagController, :show
      end

      get "/fronting", System.FrontingController, :index
    end
  end

  scope "/auth", OctoconWeb do
    pipe_through :browser

    get "/:provider", AuthController, :request
    get "/:provider/callback", AuthController, :callback
    post "/:provider/callback", AuthController, :callback
  end

  scope "/auth/link", OctoconWeb do
    pipe_through [:browser]

    get "/:provider", AuthLinkController, :request
    get "/:provider/callback", AuthLinkController, :callback
    post "/:provider/callback", AuthLinkController, :callback
  end

  scope "/admin" do
    pipe_through [:browser, :admins_only]

    live_dashboard "/dashboard", metrics: OctoconWeb.Telemetry
  end

  defp admin_basic_auth(conn, _opts) do
    username = System.fetch_env!("ADMIN_USERNAME")
    password = System.fetch_env!("ADMIN_PASSWORD")
    Plug.BasicAuth.basic_auth(conn, username: username, password: password)
  end
end
