defmodule OctoconWeb.AuthPipeline do
  @moduledoc false
  use Guardian.Plug.Pipeline, otp_app: :octocon

  plug Guardian.Plug.VerifyHeader, realm: "Bearer", claims: %{"typ" => "access"}
  plug Guardian.Plug.LoadResource, allow_blank: true
end
